using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;

namespace SMDWin.Services
{
    public static class FileSignatureService
    {
        public readonly struct SignInfo
        {
            /// <summary>"signed" | "untrusted" | "unsigned" | "" (not checked yet / error).</summary>
            public string Status    { get; init; }
            public string Publisher { get; init; }
        }

        private static readonly SignInfo Empty = new() { Status = "", Publisher = "" };
        private static readonly string OwnExe = Environment.ProcessPath ?? "";

        private sealed class CacheEntry
        {
            public DateTime LastWriteUtc;
            public SignInfo Info;
        }

        private static readonly ConcurrentDictionary<string, CacheEntry> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Returns signature info for the path, using the cache if the file has not changed since the last check.
        /// computeBudget is decremented ONLY when a new check is really done (cache miss) — when it reaches 0, the remaining
        /// processes in this tick stay "" (unchecked) and are computed on the next tick, when the budget resets. This avoids
        /// a visible freeze on the first refresh after start, when the whole cache is empty and hundreds of exes would need
        /// checking at once.
        /// </summary>
        public static SignInfo GetSignInfo(string path, ref int computeBudget)
        {
            if (string.IsNullOrWhiteSpace(path)) return Empty;
            // SMD Win itself: one self-contained exe of 100+ MB — checking it read the whole file and held the process list for
            // 10.5 s (16.11.2026 log). It is not signed yet, so the answer is known.
            if (string.Equals(path, OwnExe, StringComparison.OrdinalIgnoreCase)) return Empty;

            DateTime lastWrite;
            try { lastWrite = File.GetLastWriteTimeUtc(path); }
            catch { return Empty; }

            if (_cache.TryGetValue(path, out var cached) && cached.LastWriteUtc == lastWrite)
                return cached.Info;

            if (computeBudget <= 0)
                return Empty; // left for the next tick

            computeBudget--;

            SignInfo info;
            try
            {
                info = VerifyWithWinTrust(path);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
            {
                info = VerifyWithX509(path);   // fallback (should not happen on Windows 8+)
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"FileSignatureService: check failed for '{path}': {ex.Message}");
                info = Empty;
            }

            _cache[path] = new CacheEntry { LastWriteUtc = lastWrite, Info = info };
            return info;
        }

        // ── Check through WinVerifyTrust ─────────────────────────────
        // Many Windows components (e.g. TextInputHost) are signed through a CATALOG (.cat in System32\CatRoot) and have no
        // signature in the file; old Microsoft signatures have an expired certificate that is still valid through the
        // timestamp. WinVerifyTrust is the official Windows check: it honours the timestamp and, together with the catalog
        // lookup, also recognises catalog-signed files. No revocation check (no network access — the table is live).

        private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        private const uint WTD_UI_NONE = 2, WTD_REVOKE_NONE = 0, WTD_CHOICE_FILE = 1, WTD_CHOICE_CATALOG = 2;
        private const uint WTD_STATEACTION_VERIFY = 1, WTD_STATEACTION_CLOSE = 2;
        private const uint WTD_REVOCATION_CHECK_NONE = 0x10, WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;
        private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_CATALOG_INFO
        {
            public uint cbStruct;
            public uint dwCatalogVersion;
            public string pcwszCatalogFilePath;
            public string pcwszMemberTag;
            public string pcwszMemberFilePath;
            public IntPtr hMemberFile;
            public IntPtr pbCalculatedFileHash;
            public uint cbCalculatedFileHash;
            public IntPtr pcCatalogContext;
            public IntPtr hCatAdmin;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pUnion;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CATALOG_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string wszCatalogFile;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WINTRUST_DATA pWVTData);

        [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptCATAdminAcquireContext2(out IntPtr phCatAdmin, IntPtr pgSubsystem, string? pwszHashAlgorithm, IntPtr pStrongHashPolicy, uint dwFlags);
        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr hCatAdmin, IntPtr hFile, ref uint pcbHash, byte[]? pbHash, uint dwFlags);
        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr hCatAdmin, byte[] pbHash, uint cbHash, uint dwFlags, IntPtr phPrevCatInfo);
        [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptCATCatalogInfoFromContext(IntPtr hCatInfo, ref CATALOG_INFO psCatInfo, uint dwFlags);
        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr hCatAdmin, IntPtr hCatInfo, uint dwFlags);
        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);

        private static int RunWinVerifyTrust(uint unionChoice, IntPtr pUnion)
        {
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = unionChoice,
                pUnion = pUnion,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL,
            };
            int result = WinVerifyTrust(IntPtr.Zero, WintrustActionGenericVerifyV2, ref data);
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, WintrustActionGenericVerifyV2, ref data);
            return result;
        }

        private static string PublisherOf(string signedFile)
        {
            try
            {
                using var c = new X509Certificate2(X509Certificate.CreateFromSignedFile(signedFile));
                return c.GetNameInfo(X509NameType.SimpleName, false) ?? "";
            }
            catch { return ""; }
        }

        private static SignInfo VerifyWithWinTrust(string path)
        {
            // 1) The signature embedded in the file
            var fi = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            int embedded;
            try
            {
                Marshal.StructureToPtr(fi, pFile, false);
                embedded = RunWinVerifyTrust(WTD_CHOICE_FILE, pFile);
            }
            finally { Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile); Marshal.FreeHGlobal(pFile); }

            if (embedded == 0)
                return new SignInfo { Status = "signed", Publisher = PublisherOf(path) };
            if (embedded != TRUST_E_NOSIGNATURE)
                return new SignInfo { Status = "untrusted", Publisher = PublisherOf(path) };

            // 2) No signature in the file → look the file up in a Windows catalog (SHA256, then SHA1)
            foreach (var alg in new[] { "SHA256", null })
            {
                var cat = VerifyViaCatalog(path, alg);
                if (cat.HasValue) return cat.Value;
            }
            return new SignInfo { Status = "unsigned", Publisher = "" };
        }

        private static SignInfo? VerifyViaCatalog(string path, string? hashAlg)
        {
            if (!CryptCATAdminAcquireContext2(out var hCatAdmin, IntPtr.Zero, hashAlg, IntPtr.Zero, 0)) return null;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                IntPtr hFile = fs.SafeFileHandle.DangerousGetHandle();
                uint cb = 0;
                CryptCATAdminCalcHashFromFileHandle2(hCatAdmin, hFile, ref cb, null, 0);
                if (cb == 0 || cb > 256) return null;
                var hash = new byte[cb];
                if (!CryptCATAdminCalcHashFromFileHandle2(hCatAdmin, hFile, ref cb, hash, 0)) return null;

                IntPtr hCatInfo = CryptCATAdminEnumCatalogFromHash(hCatAdmin, hash, cb, 0, IntPtr.Zero);
                if (hCatInfo == IntPtr.Zero) return null;
                try
                {
                    var ci = new CATALOG_INFO { cbStruct = (uint)Marshal.SizeOf<CATALOG_INFO>(), wszCatalogFile = "" };
                    if (!CryptCATCatalogInfoFromContext(hCatInfo, ref ci, 0) || string.IsNullOrEmpty(ci.wszCatalogFile)) return null;

                    var wci = new WINTRUST_CATALOG_INFO
                    {
                        cbStruct = (uint)Marshal.SizeOf<WINTRUST_CATALOG_INFO>(),
                        pcwszCatalogFilePath = ci.wszCatalogFile,
                        pcwszMemberTag = Convert.ToHexString(hash),
                        pcwszMemberFilePath = path,
                        hMemberFile = hFile,
                        hCatAdmin = hCatAdmin,
                    };
                    IntPtr pCat = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_CATALOG_INFO>());
                    int r;
                    try
                    {
                        Marshal.StructureToPtr(wci, pCat, false);
                        r = RunWinVerifyTrust(WTD_CHOICE_CATALOG, pCat);
                    }
                    finally { Marshal.DestroyStructure<WINTRUST_CATALOG_INFO>(pCat); Marshal.FreeHGlobal(pCat); }

                    // The catalog is signed (usually by Microsoft Windows) — the publisher comes from the catalog
                    return new SignInfo { Status = r == 0 ? "signed" : "untrusted", Publisher = PublisherOf(ci.wszCatalogFile) };
                }
                finally { CryptCATAdminReleaseCatalogContext(hCatAdmin, hCatInfo, 0); }
            }
            catch { return null; }
            finally { CryptCATAdminReleaseContext(hCatAdmin, 0); }
        }

        /// <summary>The older method (fallback): only the embedded signature + X509Chain.</summary>
        private static SignInfo VerifyWithX509(string path)
        {
            try
            {
                using var cert2 = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                // NotTimeValid: expired certificate but a timestamped signature — valid
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;
                bool chainOk = chain.Build(cert2);
                if (!chainOk)
                    chainOk = chain.ChainStatus.Length > 0 && chain.ChainStatus.All(st =>
                        st.Status is X509ChainStatusFlags.RevocationStatusUnknown or X509ChainStatusFlags.OfflineRevocation
                                  or X509ChainStatusFlags.PartialChain or X509ChainStatusFlags.NotTimeValid);
                return new SignInfo { Status = chainOk ? "signed" : "untrusted", Publisher = cert2.GetNameInfo(X509NameType.SimpleName, false) ?? "" };
            }
            catch (CryptographicException) { return new SignInfo { Status = "unsigned", Publisher = "" }; }
        }

    }
}
