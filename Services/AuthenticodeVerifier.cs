using System;
using System.Runtime.InteropServices;

namespace SMDWin.Services
{
    /// <summary>
    /// Real Authenticode check (signature + chain of trust + the file hash matches the signed hash) before running
    /// any executable downloaded from the internet or extracted from a resource, especially when it will run as Administrator.
    ///
    /// Why WinVerifyTrust and not X509Certificate2.CreateFromSignedFile: that method only EXTRACTS the certificate,
    /// without checking whether the file changed after signing or whether the chain is trusted — a file modified after
    /// signing would still "pass". WinVerifyTrust is the mechanism used by Windows Explorer ("Digital Signatures" tab)
    /// and by sigcheck / signtool /verify: it checks the hash, the chain up to a trusted root in the Windows store,
    /// and that the certificate was not revoked or expired at signing time.
    ///
    /// A file replaced between extraction/download and execution (the classic TOCTOU race on a fixed-name file in
    /// %TEMP%, writable by any process of the same user) fails this check, because it no longer carries the
    /// original publisher's valid Authenticode signature.
    /// </summary>
    public static class AuthenticodeVerifier
    {
        private const int WTD_UI_NONE = 2;
        private const int WTD_REVOKE_NONE = 0;
        private const int WTD_CHOICE_FILE = 1;
        private const int WTD_STATEACTION_VERIFY = 1;
        private const int WTD_STATEACTION_CLOSE = 2;
        private const int WTD_SAFER_FLAG = 0x100;
        private const int WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000; // no network access for CRL — offline-friendly check

        private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
            new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
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
            public IntPtr pFile; // pointer to WINTRUST_FILE_INFO
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            [MarshalAs(UnmanagedType.LPWStr)] public string? pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
        private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WINTRUST_DATA pWVTData);

        /// <summary>
        /// True if the file has a valid Authenticode signature that chains to a trusted Windows root and whose hash matches
        /// the file content on disk right now. False for anything else — unsigned, invalid signature, modified after signing,
        /// or a verification error (when in doubt, treat as unsafe).
        /// </summary>
        public static bool IsValidAndTrusted(string filePath, out string errorDetail)
        {
            errorDetail = "";
            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
            {
                errorDetail = "file not found";
                return false;
            }

            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = filePath,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero,
            };

            IntPtr pFileInfo = IntPtr.Zero;
            try
            {
                pFileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
                Marshal.StructureToPtr(fileInfo, pFileInfo, false);

                var wtData = new WINTRUST_DATA
                {
                    cbStruct             = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    pPolicyCallbackData  = IntPtr.Zero,
                    pSIPClientData       = IntPtr.Zero,
                    dwUIChoice           = WTD_UI_NONE,
                    fdwRevocationChecks  = WTD_REVOKE_NONE,
                    dwUnionChoice        = WTD_CHOICE_FILE,
                    pFile                = pFileInfo,
                    dwStateAction        = WTD_STATEACTION_VERIFY,
                    hWVTStateData        = IntPtr.Zero,
                    pwszURLReference     = null,
                    dwProvFlags          = WTD_SAFER_FLAG | WTD_CACHE_ONLY_URL_RETRIEVAL,
                    dwUIContext          = 0,
                    pSignatureSettings   = IntPtr.Zero,
                };

                int result = WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, ref wtData);

                // Release the WinTrust internal state (required after VERIFY) — always, whatever the result.
                wtData.dwStateAction = WTD_STATEACTION_CLOSE;
                try { WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, ref wtData); }
                catch (Exception ex) { AppLogger.Debug("AuthenticodeVerifier cleanup: " + ex.Message); }

                if (result == 0) return true; // ERROR_SUCCESS

                errorDetail = $"WinVerifyTrust returned 0x{result:X8}";
                return false;
            }
            catch (Exception ex)
            {
                errorDetail = ex.Message;
                AppLogger.Warning(ex, "AuthenticodeVerifier.IsValidAndTrusted");
                return false;
            }
            finally
            {
                if (pFileInfo != IntPtr.Zero) Marshal.FreeHGlobal(pFileInfo);
            }
        }
    }
}
