// Copyright 2026 Eickter Software & Supplies
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace EtaSignAgent;

/// <summary>
/// Rate-limits pairing attempts.
///
/// The pairing code is six digits, and pairing is what stands between a web
/// page and a request to seal a document with the taxpayer's e-seal. Without a
/// limit, a page left open in the operator's browser can work through all
/// 900,000 codes over loopback in minutes, then poll /v1/ping until the token
/// is unlocked — which at a till is most of the working day.
///
/// Five attempts, then five minutes of nothing. That leaves a genuine operator
/// who mistyped a code an obvious message and a short wait, and it puts the
/// expected time to guess a code somewhere past a year and a half.
///
/// Note what this deliberately does NOT do: issue a new pairing code when the
/// allowance runs out. Rotating sounds stronger and is not — each guess is
/// still 1 in 900,000, so an attacker needs the same number of attempts either
/// way — while an operator staring at a code that silently stopped working has
/// been handed a mystery. The wait is the control; the code is the operator's
/// reference and stays put.
///
/// In memory rather than in the config file, on purpose: the counter only has
/// to outlive the attack, and anything able to restart the agent to clear it is
/// already running as the operator and past this boundary anyway.
/// </summary>
public sealed class PairingGuard
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private int _failed;
    private DateTime _lockedUntil = DateTime.MinValue;

    /// <summary>
    /// How long pairing stays refused, or null if it is open. Kestrel serves
    /// requests concurrently, so this is read under the same lock that writes
    /// it — otherwise a burst of parallel guesses races past the counter.
    /// </summary>
    public TimeSpan? LockedFor
    {
        get
        {
            lock (_gate)
            {
                var remaining = _lockedUntil - DateTime.UtcNow;
                return remaining > TimeSpan.Zero ? remaining : null;
            }
        }
    }

    /// <summary>Records a correct code, clearing the allowance.</summary>
    public void Succeeded()
    {
        lock (_gate)
        {
            _failed = 0;
            _lockedUntil = DateTime.MinValue;
        }
    }

    /// <summary>Records a wrong code, starting the cooldown once they run out.</summary>
    public void Failed()
    {
        lock (_gate)
        {
            if (++_failed < MaxAttempts) return;

            _failed = 0;
            _lockedUntil = DateTime.UtcNow + Cooldown;
        }
    }
}
