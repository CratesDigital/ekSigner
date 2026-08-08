using Net.Pkcs11Interop.Common;
using Net.Pkcs11Interop.HighLevelAPI;

namespace EtaSignAgent;

public sealed record TokenCertificate(
    string Thumbprint,
    string TokenLabel,
    byte[] CkaId,
    byte[] Der,
    string Subject,
    string Issuer,
    DateTime NotAfter)
{
    public bool Expired => NotAfter < DateTime.UtcNow;
}

/// <summary>
/// Owns every interaction with the smart tokens.
///
/// All token work is funnelled through one lock. Vendor PKCS#11 modules are
/// frequently not thread-safe, and an HTTP server is concurrent by nature —
/// without this, two overlapping sign requests would enter the same module on
/// different threads. The modules are loaded once and kept, but SESSIONS are
/// short: opened to do work, closed straight after.
/// </summary>
public sealed class TokenService : IDisposable
{
    private readonly Pkcs11InteropFactories _factories = new();
    private readonly List<IPkcs11Library> _libraries = new();
    private readonly object _gate = new();

    private string? _pin;
    private DateTime _unlockedUntil = DateTime.MinValue;

    public IReadOnlyList<string> LoadedModules { get; }

    public TokenService(IEnumerable<string> modulePaths)
    {
        var loaded = new List<string>();

        foreach (var path in modulePaths.Where(File.Exists).Distinct())
        {
            try
            {
                // AppType.SingleThreaded: CKF_OS_LOCKING_OK is not set, because
                // we serialise access ourselves rather than trusting the module
                // to lock correctly.
                _libraries.Add(_factories.Pkcs11LibraryFactory.LoadPkcs11Library(
                    _factories, path, AppType.SingleThreaded));
                loaded.Add(path);
            }
            catch (Exception ex)
            {
                // A vendor module that fails to load must not stop the other
                // one working — a machine may have drivers for a token it does
                // not currently hold.
                Console.Error.WriteLine($"[token] could not load {path}: {ex.Message}");
            }
        }

        LoadedModules = loaded;
    }

    public bool IsUnlocked
    {
        get { lock (_gate) { return _pin is not null && DateTime.UtcNow < _unlockedUntil; } }
    }

    public DateTime? UnlockedUntil
    {
        get { lock (_gate) { return IsUnlocked ? _unlockedUntil : null; } }
    }

    public void Lock()
    {
        lock (_gate) { _pin = null; _unlockedUntil = DateTime.MinValue; }
    }

    /// <summary>
    /// Verify a PIN by logging in once, then hold it for the session.
    ///
    /// Exactly one C_Login attempt. Neither supported token reports
    /// CKF_USER_PIN_FINAL_TRY, so nothing warns before a wrong PIN locks the
    /// token permanently — a retry loop here would eventually brick a
    /// taxpayer's e-seal.
    /// </summary>
    public void Unlock(string pin, TimeSpan duration)
    {
        lock (_gate)
        {
            var slot = FindSlots().FirstOrDefault()
                ?? throw new InvalidOperationException("No token is present.");

            using var session = slot.OpenSession(SessionType.ReadOnly);
            session.Login(CKU.CKU_USER, pin);
            session.Logout();

            _pin = pin;
            _unlockedUntil = DateTime.UtcNow.Add(duration);
        }
    }

    /// <summary>
    /// Certificates across every present token, newest expiry first.
    ///
    /// Enumerates CERTIFICATES and resolves keys from them, never the reverse:
    /// PROXKey exposes a private key whose certificate it does not surface, and
    /// starting from keys would offer an entry that can never be used.
    /// Certificates are public objects, so this needs no PIN.
    /// </summary>
    public List<TokenCertificate> ListCertificates()
    {
        lock (_gate)
        {
            var found = new List<TokenCertificate>();

            foreach (var slot in FindSlots())
            {
                var label = slot.GetTokenInfo().Label.Trim();
                using var session = slot.OpenSession(SessionType.ReadOnly);

                var template = new List<IObjectAttribute>
                {
                    _factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_CERTIFICATE),
                    _factories.ObjectAttributeFactory.Create(CKA.CKA_CERTIFICATE_TYPE, CKC.CKC_X_509),
                };

                foreach (var handle in session.FindAllObjects(template))
                {
                    var attrs = session.GetAttributeValue(handle, new List<CKA> { CKA.CKA_ID, CKA.CKA_VALUE });
                    var der = attrs[1].GetValueAsByteArray();
                    var parsed = new Org.BouncyCastle.X509.X509CertificateParser().ReadCertificate(der);

                    found.Add(new TokenCertificate(
                        Thumbprint: Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(der)),
                        TokenLabel: label,
                        CkaId: attrs[0].GetValueAsByteArray(),
                        Der: der,
                        // CKA_LABEL is empty on PROXKey and a container GUID on
                        // ePass2003, so neither is worth showing. The subject
                        // comes off the certificate itself.
                        Subject: parsed.SubjectDN.ToString(),
                        Issuer: parsed.IssuerDN.ToString(),
                        NotAfter: parsed.NotAfter.ToUniversalTime()));
                }
            }

            return found.OrderByDescending(c => c.NotAfter).ToList();
        }
    }

    /// <summary>Seal a canonical ETA document with the chosen certificate.</summary>
    public string Sign(string thumbprint, string canonical)
    {
        lock (_gate)
        {
            if (_pin is null || DateTime.UtcNow >= _unlockedUntil)
            {
                throw new TokenLockedException();
            }

            var certificate = ListCertificatesUnlocked().FirstOrDefault(c =>
                string.Equals(c.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("That certificate is not on any token currently present.");

            if (certificate.Expired)
            {
                throw new InvalidOperationException(
                    $"That certificate expired on {certificate.NotAfter:yyyy-MM-dd} and can no longer sign.");
            }

            return EtaCades.Sign(canonical, certificate.Der,
                data => SignWithKey(certificate.CkaId, data));
        }
    }

    private byte[] SignWithKey(byte[] ckaId, byte[] data)
    {
        foreach (var slot in FindSlots())
        {
            if (!slot.GetMechanismList().Contains(CKM.CKM_SHA256_RSA_PKCS))
            {
                continue;
            }

            using var session = slot.OpenSession(SessionType.ReadOnly);
            session.Login(CKU.CKU_USER, _pin);

            try
            {
                var key = session.FindAllObjects(new List<IObjectAttribute>
                {
                    _factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_PRIVATE_KEY),
                    _factories.ObjectAttributeFactory.Create(CKA.CKA_ID, ckaId),
                    _factories.ObjectAttributeFactory.Create(CKA.CKA_SIGN, true),
                }).FirstOrDefault();

                if (key is null)
                {
                    continue;   // the certificate belongs to a different slot
                }

                // Both supported tokens advertise CKM_SHA256_RSA_PKCS, so the
                // token hashes and signs the SignedAttrs in one call and there
                // is no DigestInfo to assemble.
                var mechanism = _factories.MechanismFactory.Create(CKM.CKM_SHA256_RSA_PKCS);
                return session.Sign(mechanism, key, data);
            }
            finally
            {
                session.Logout();
            }
        }

        throw new InvalidOperationException("No signing key on a present token matches that certificate.");
    }

    private List<TokenCertificate> ListCertificatesUnlocked()
    {
        // Called from inside the lock; ListCertificates() would deadlock on a
        // non-reentrant lock, so the enumeration is inlined here.
        var found = new List<TokenCertificate>();
        foreach (var slot in FindSlots())
        {
            var label = slot.GetTokenInfo().Label.Trim();
            using var session = slot.OpenSession(SessionType.ReadOnly);
            var template = new List<IObjectAttribute>
            {
                _factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_CERTIFICATE),
                _factories.ObjectAttributeFactory.Create(CKA.CKA_CERTIFICATE_TYPE, CKC.CKC_X_509),
            };
            foreach (var handle in session.FindAllObjects(template))
            {
                var attrs = session.GetAttributeValue(handle, new List<CKA> { CKA.CKA_ID, CKA.CKA_VALUE });
                var der = attrs[1].GetValueAsByteArray();
                var parsed = new Org.BouncyCastle.X509.X509CertificateParser().ReadCertificate(der);
                found.Add(new TokenCertificate(
                    Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(der)),
                    label, attrs[0].GetValueAsByteArray(), der,
                    parsed.SubjectDN.ToString(), parsed.IssuerDN.ToString(),
                    parsed.NotAfter.ToUniversalTime()));
            }
        }
        return found;
    }

    private List<ISlot> FindSlots()
        => _libraries.SelectMany(l => l.GetSlotList(SlotsType.WithTokenPresent)).ToList();

    public void Dispose()
    {
        Lock();
        foreach (var library in _libraries)
        {
            library.Dispose();
        }
    }
}

public sealed class TokenLockedException : Exception
{
    public TokenLockedException() : base("The signing token is locked.") { }
}
