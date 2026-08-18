using System.Security.Cryptography;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Cms;
using Org.BouncyCastle.Asn1.Ess;
using Org.BouncyCastle.Asn1.Nist;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;

// Asn1.Cms, Asn1.Pkcs and Asn1.X509 each declare types of the same name, so
// every one of these has to be pinned explicitly. The CMS variants are the
// right ones throughout: this builds a CMS SignedData, and the Pkcs namespace's
// near-identical types belong to the older PKCS#7 definitions.
using Attribute = Org.BouncyCastle.Asn1.Cms.Attribute;
using ContentInfo = Org.BouncyCastle.Asn1.Cms.ContentInfo;
using IssuerAndSerialNumber = Org.BouncyCastle.Asn1.Cms.IssuerAndSerialNumber;
using SignedData = Org.BouncyCastle.Asn1.Cms.SignedData;
using SignerInfo = Org.BouncyCastle.Asn1.Cms.SignerInfo;
using Time = Org.BouncyCastle.Asn1.Cms.Time;

namespace EtaSignAgent;

/// <summary>
/// Builds the CAdES-BES detached signature ETA expects.
///
/// The structure is prescribed field by field by ITIDA's "Digital Signature
/// Format for E-Invoice System" v1.1, and one requirement is unusual enough that
/// generic tooling gets it wrong: eContentType is digestedData, not id-data.
/// That single field is why `openssl cms -sign -cades` cannot be used here — it
/// adds the right signing-certificate attribute but always emits id-data.
///
/// Everything is assembled from BouncyCastle's ASN.1 types rather than
/// CmsSignedDataGenerator, because the high-level API gives no way to override
/// the content type and no way to delegate the RSA operation to a smart token.
/// </summary>
public static class EtaCades
{
    /// <summary>RFC 5652 id-digestedData. ETA requires this as the eContentType.</summary>
    private static readonly DerObjectIdentifier DigestedData =
        new("1.2.840.113549.1.7.5");

    /// <param name="canonical">
    /// The canonical serialization produced by the caller's EtaDocumentSerializer.
    /// Hashed here as UTF-8 — this is the only place the document is digested,
    /// which is what removes the double-hash ambiguity of the ITIDA client.
    /// </param>
    /// <param name="signerCertDer">The signing certificate, DER encoded.</param>
    /// <param name="signRsaSha256">
    /// Delegate that performs SHA-256 + PKCS#1 v1.5 over the bytes it is given,
    /// on the token. Both verified tokens advertise CKM_SHA256_RSA_PKCS, so this
    /// is a single C_Sign; the bytes handed over are the DER-encoded SignedAttrs.
    /// </param>
    /// <returns>Base64 CAdES-BES, ready for the document's `signatures` element.</returns>
    public static string Sign(
        string canonical,
        byte[] signerCertDer,
        Func<byte[], byte[]> signRsaSha256)
    {
        var messageDigest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        var certificate = X509CertificateStructure.GetInstance(Asn1Object.FromByteArray(signerCertDer));
        var sha256 = new AlgorithmIdentifier(NistObjectIdentifiers.IdSha256, DerNull.Instance);

        // ── The four signed attributes ITIDA mandates ────────────────
        // Order matters only in that DER sorts a SET; BouncyCastle's DerSet
        // handles that, so these may be added in any order.
        var attributes = new Asn1EncodableVector
        {
            new Attribute(CmsAttributes.ContentType, new DerSet(DigestedData)),
            new Attribute(CmsAttributes.SigningTime, new DerSet(new Time(DateTime.UtcNow))),
            new Attribute(CmsAttributes.MessageDigest, new DerSet(new DerOctetString(messageDigest))),
            new Attribute(
                PkcsObjectIdentifiers.IdAASigningCertificateV2,
                new DerSet(new SigningCertificateV2(new[]
                {
                    new EssCertIDv2(sha256, SHA256.HashData(signerCertDer)),
                }))),
        };
        var signedAttrs = new DerSet(attributes);

        // What actually gets signed is the SignedAttrs re-tagged as a SET OF
        // (0x31), not the [0] IMPLICIT (0xA0) form they carry inside SignerInfo.
        // GetEncoded(Der) on the DerSet yields exactly that; using the tagged
        // form instead is a classic source of signatures that verify nowhere.
        var signature = signRsaSha256(signedAttrs.GetEncoded(Asn1Encodable.Der));

        var signerInfo = new SignerInfo(
            new SignerIdentifier(new IssuerAndSerialNumber(
                certificate.Issuer,
                certificate.SerialNumber.Value)),
            sha256,
            signedAttrs,
            new AlgorithmIdentifier(PkcsObjectIdentifiers.Sha256WithRsaEncryption, DerNull.Instance),
            new DerOctetString(signature),
            unauthenticatedAttributes: null);   // ITIDA: "should not be present"

        var signedData = new SignedData(
            digestAlgorithms: new DerSet(sha256),
            // Null content: the signature is detached, so eContent is absent.
            // This also forces SignedData.version to 3, as ETA expects, because
            // RFC 5652 mandates version 3 whenever eContentType is not id-data.
            contentInfo: new ContentInfo(DigestedData, null),
            certificates: new DerSet(certificate),   // signer certificate only
            crls: null,
            signerInfos: new DerSet(signerInfo));

        var cms = new ContentInfo(CmsObjectIdentifiers.SignedData, signedData);

        return Convert.ToBase64String(cms.GetEncoded(Asn1Encodable.Der));
    }
}
