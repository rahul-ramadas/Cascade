using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Cascade.App;

/// <summary>
/// Asks Windows whether a file carries a valid Authenticode signature, and who signed it.
///
/// The question is put to WinVerifyTrust rather than answered here, because "is this signed" is not a
/// question a program can answer by reading the file: it means the digest still matches the bytes, the
/// certificate chains to a root the machine trusts, and no policy on this machine says otherwise. The
/// managed shortcut for this - X509Certificate.CreateFromSignedFile - only lifts the certificate out of the
/// file and checks none of that, so a file with any certificate stapled to it passes. It is also obsolete
/// (SYSLIB0057) and would not compile here.
/// </summary>
internal static class Authenticode
{
    /// <summary>Identifies a subscriber of Azure Artifact Signing. Every certificate the service issues for
    /// public trust carries one of these, and the value outlives any individual certificate.</summary>
    private const string DurableIdentityPrefix = "1.3.6.1.4.1.311.97.";

    /// <summary>...except this one, which every Artifact Signing public-trust certificate carries to mark
    /// itself as one. Matching on it would accept any subscriber of the service, which is everyone.</summary>
    private const string PublicTrustMarker = "1.3.6.1.4.1.311.97.1.0";

    /// <summary>
    /// Who signed this file, in a form that stays the same as certificates come and go - or null if the file
    /// is not validly signed at all, which is the same answer as "signed by nobody".
    ///
    /// Artifact Signing issues certificates that live 72 hours and renews them daily, so a thumbprint or a
    /// public key identifies a particular Tuesday rather than a signer; Microsoft says as much and puts a
    /// durable value in a custom EKU for exactly this purpose. That is what this returns when it is there.
    /// A certificate from anywhere else falls back to its subject, which is the best on offer.
    /// </summary>
    public static string? IdentityOf(string path)
    {
        try
        {
            using var cert = VerifyAndExtract(Path.GetFullPath(path));
            if (cert is null) return null;

            var durable = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                              .SelectMany(e => e.EnhancedKeyUsages.Cast<Oid>())
                              .Select(o => o.Value)
                              .Where(v => v is not null
                                       && v.StartsWith(DurableIdentityPrefix, StringComparison.Ordinal)
                                       && v != PublicTrustMarker)
                              .Order(StringComparer.Ordinal)
                              .ToArray();

            return durable.Length > 0 ? string.Join(",", durable) : "subject:" + cert.Subject;
        }
        catch { return null; }
    }

    /// <summary>
    /// One WinVerifyTrust call, and the certificate it decided to trust read back out of the state it built.
    /// Taking the certificate from there rather than re-opening the file matters: it is then necessarily the
    /// one that was just verified, and not whatever a second read of the file might turn up.
    /// </summary>
    private static X509Certificate2? VerifyAndExtract(string fullPath)
    {
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = fullPath
        };

        IntPtr fileInfoPtr = Marshal.AllocHGlobal((int)fileInfo.cbStruct);
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, fDeleteOld: false);

            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = WtdUiNone,
                // Revocation is left to Windows at the moment the file is executed. This runs at startup, and
                // reaching for a CRL here would let a slow or captive network stall the launch; a cache-only
                // answer, meanwhile, is as often "do not know" as "not revoked" and would refuse good updates.
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pFile = fileInfoPtr,
                dwStateAction = WtdStateActionVerify,
                // WTD_LIFETIME_SIGNING_FLAG is deliberately absent. It would tie a signature's validity to the
                // life of the certificate that made it, and these certificates expire in 72 hours - every
                // release would stop verifying within days of being published. Without it a countersigned
                // timestamp keeps a signature good, which is the whole reason the build insists on one.
                dwProvFlags = WtdCacheOnlyUrlRetrieval
            };

            IntPtr dataPtr = Marshal.AllocHGlobal((int)data.cbStruct);
            try
            {
                Marshal.StructureToPtr(data, dataPtr, fDeleteOld: false);

                var action = ActionGenericVerifyV2;
                if (WinVerifyTrust(IntPtr.Zero, ref action, dataPtr) != 0) return null;

                // Read the state back: StructureToPtr copied a snapshot in, and the handle we need was
                // written to the native copy.
                var verified = Marshal.PtrToStructure<WinTrustData>(dataPtr);
                try { return SignerCertificate(verified.hWVTStateData); }
                finally
                {
                    // Closing releases the state handle; nothing can be done about a failure to, and the
                    // verdict has already been reached, so the code is noted rather than acted on.
                    verified.dwStateAction = WtdStateActionClose;
                    Marshal.StructureToPtr(verified, dataPtr, fDeleteOld: false);
                    _ = WinVerifyTrust(IntPtr.Zero, ref action, dataPtr);
                }
            }
            finally { Marshal.FreeHGlobal(dataPtr); }
        }
        finally { Marshal.FreeHGlobal(fileInfoPtr); }
    }

    /// <summary>The signing certificate out of a verified state handle, copied before the state is closed.</summary>
    private static X509Certificate2? SignerCertificate(IntPtr stateData)
    {
        if (stateData == IntPtr.Zero) return null;

        IntPtr provider = WTHelperProvDataFromStateData(stateData);
        if (provider == IntPtr.Zero) return null;

        IntPtr signer = WTHelperGetProvSignerFromChain(provider, 0, fCounterSigner: false, 0);
        if (signer == IntPtr.Zero) return null;

        IntPtr providerCert = WTHelperGetProvCertFromChain(signer, 0);
        if (providerCert == IntPtr.Zero) return null;

        var wrapped = Marshal.PtrToStructure<CryptProviderCert>(providerCert);
        if (wrapped.pCert == IntPtr.Zero) return null;

        var context = Marshal.PtrToStructure<CertContext>(wrapped.pCert);
        if (context.pbCertEncoded == IntPtr.Zero || context.cbCertEncoded == 0) return null;

        byte[] encoded = new byte[context.cbCertEncoded];
        Marshal.Copy(context.pbCertEncoded, encoded, 0, encoded.Length);
        return X509CertificateLoader.LoadCertificate(encoded);
    }

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdCacheOnlyUrlRetrieval = 0x00001000;

    private static Guid ActionGenericVerifyV2 => new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    // Searched for beside the executable first if named alone, and Cascade is a single file people copy into
    // whatever folder suits them - so without this the folder it sits in could supply the library that
    // answers "is this file trustworthy".
    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, IntPtr data);

    [DllImport("wintrust.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [DllImport("wintrust.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signerIndex,
                                                                [MarshalAs(UnmanagedType.Bool)] bool fCounterSigner,
                                                                uint counterSignerIndex);

    [DllImport("wintrust.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    /// <summary>The leading fields of CRYPT_PROVIDER_CERT; the rest is not needed and not declared.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderCert
    {
        public uint cbStruct;
        public IntPtr pCert;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertContext
    {
        public uint dwCertEncodingType;
        public IntPtr pbCertEncoded;
        public uint cbCertEncoded;
        public IntPtr pCertInfo;
        public IntPtr hCertStore;
    }
}
