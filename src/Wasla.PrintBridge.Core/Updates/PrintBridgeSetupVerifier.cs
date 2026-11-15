using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Wasla.PrintBridge.Updates;

/// <summary>The outcome of one Authenticode check.</summary>
public enum PrintBridgeSignatureStatus
{
    /// <summary>Validly signed: a code-signing certificate chaining to a trusted root, not revoked.</summary>
    Valid,

    NotSigned,

    /// <summary>Signed, but the signature does not verify (untrusted root, revoked, expired, tampered, revocation unknown).</summary>
    Invalid
}

/// <summary>A publisher as Authenticode identifies it: the signer certificate's subject (WAS-63 trust anchor).</summary>
public sealed class PrintBridgePublisher
{
    private readonly byte[] _rawSubject;

    public PrintBridgePublisher(X500DistinguishedName subject)
    {
        _rawSubject = subject.RawData.ToArray();
        Subject = subject.Name;
    }

    /// <summary>The subject as text, for logs and support only (it is not secret).</summary>
    public string Subject { get; }

    /// <summary>The same subject, compared byte for byte in its encoded form (no text normalization).</summary>
    public bool IsSameAs(PrintBridgePublisher other) => _rawSubject.AsSpan().SequenceEqual(other._rawSubject);
}

/// <summary>
/// Authenticode through Windows (<c>WinVerifyTrust</c>, Authenticode policy): the same check Windows makes before it runs a
/// signed program, here with revocation checked for the whole chain except the root.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PrintBridgeAuthenticode
{
    /// <summary>Verifies <paramref name="path"/>'s embedded signature and returns its publisher when it is valid.</summary>
    public static PrintBridgeSignatureStatus Verify(string path, out PrintBridgePublisher? publisher)
    {
        publisher = null;
        var status = WinVerifyTrust(path);
        if (status != PrintBridgeSignatureStatus.Valid)
            return status;

        try
        {
#pragma warning disable SYSLIB0057 // The signer certificate of an Authenticode file; X509CertificateLoader has no equivalent.
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            publisher = new PrintBridgePublisher(signer.SubjectName);
            return PrintBridgeSignatureStatus.Valid;
        }
        catch (CryptographicException)
        {
            return PrintBridgeSignatureStatus.Invalid;
        }
    }

    /// <summary>
    /// The publisher that signed this running program (the installed Print Bridge), or null when it is not validly signed:
    /// such an installation (every development build) cannot update itself.
    /// </summary>
    public static PrintBridgePublisher? OfRunningExecutable()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
            return null;

        return Verify(path, out var publisher) == PrintBridgeSignatureStatus.Valid ? publisher : null;
    }

    private static PrintBridgeSignatureStatus WinVerifyTrust(string path)
    {
        var fileInfo = new WinTrustFileInfo(path);
        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        var data = new WinTrustData(fileInfoPointer);
        var dataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            Marshal.StructureToPtr(data, dataPointer, fDeleteOld: false);
            var result = NativeWinVerifyTrust(IntPtr.Zero, GenericVerifyV2, dataPointer);

            // Release what the verification allocated, whatever its result.
            data = Marshal.PtrToStructure<WinTrustData>(dataPointer);
            data.StateAction = StateActionClose;
            Marshal.StructureToPtr(data, dataPointer, fDeleteOld: false);
            NativeWinVerifyTrust(IntPtr.Zero, GenericVerifyV2, dataPointer);

            return result switch
            {
                0 => PrintBridgeSignatureStatus.Valid,
                TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown => PrintBridgeSignatureStatus.NotSigned,
                _ => PrintBridgeSignatureStatus.Invalid
            };
        }
        finally
        {
            Marshal.FreeHGlobal(dataPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
            fileInfo.Dispose();
        }
    }

    private const uint UiNone = 2;
    private const uint RevokeWholeChain = 1;
    private const uint ChoiceFile = 1;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;
    private const uint RevocationCheckChainExcludeRoot = 0x80;
    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [DllImport("wintrust.dll", EntryPoint = "WinVerifyTrust", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int NativeWinVerifyTrust(IntPtr window, [MarshalAs(UnmanagedType.LPStruct)] Guid action, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo : IDisposable
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr File;
        public IntPtr KnownSubject;

        public WinTrustFileInfo(string path)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            FilePath = Marshal.StringToCoTaskMemUni(path);
            File = IntPtr.Zero;
            KnownSubject = IntPtr.Zero;
        }

        public void Dispose()
        {
            if (FilePath != IntPtr.Zero)
                Marshal.FreeCoTaskMem(FilePath);
            FilePath = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;

        public WinTrustData(IntPtr fileInfo)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = UiNone;
            RevocationChecks = RevokeWholeChain;
            UnionChoice = ChoiceFile;
            FileInfo = fileInfo;
            StateAction = StateActionVerify;
            StateData = IntPtr.Zero;
            UrlReference = IntPtr.Zero;
            ProviderFlags = RevocationCheckChainExcludeRoot;
            UiContext = 0;
            SignatureSettings = IntPtr.Zero;
        }
    }
}

/// <summary>The outcome of verifying a downloaded setup (WAS-63). Anything but <see cref="Verified"/> means it is never run.</summary>
public enum PrintBridgeSetupVerification
{
    Verified,
    Missing,
    SizeMismatch,
    HashMismatch,
    NotSigned,
    SignatureInvalid,
    PublisherMismatch,
    VersionMismatch
}

/// <summary>
/// Decides whether a setup file may run as the update to <c>release</c> (docs/integrations/print-bridge-updates.md, trust
/// model): exactly the described size and SHA-256 (integrity), a valid Authenticode signature by the publisher that signed the
/// installed Print Bridge (authenticity), and a version resource equal to the described version and newer than the
/// installed one. The file is held open against writers for the whole check. The service runs it after the download, and
/// the app again right before it starts the setup.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PrintBridgeSetupVerifier
{
    public static PrintBridgeSetupVerification Verify(
        string path,
        PrintBridgeRelease release,
        PrintBridgePublisher installedPublisher,
        Version installedVersion)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(installedPublisher);

        FileStream file;
        try
        {
            // No one may write the file while it is checked (the updates folder already keeps local users out).
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PrintBridgeSetupVerification.Missing;
        }

        using (file)
        {
            if (file.Length != release.SetupBytes)
                return PrintBridgeSetupVerification.SizeMismatch;

            var hash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
            if (!string.Equals(hash, release.SetupSha256, StringComparison.Ordinal))
                return PrintBridgeSetupVerification.HashMismatch;

            switch (PrintBridgeAuthenticode.Verify(path, out var publisher))
            {
                case PrintBridgeSignatureStatus.NotSigned:
                    return PrintBridgeSetupVerification.NotSigned;
                case PrintBridgeSignatureStatus.Invalid:
                    return PrintBridgeSetupVerification.SignatureInvalid;
            }

            if (publisher is null || !publisher.IsSameAs(installedPublisher))
                return PrintBridgeSetupVerification.PublisherMismatch;

            var setupVersion = PrintBridgeReleaseVersion.FromResource(FileVersionInfo.GetVersionInfo(path).ProductVersion);
            if (setupVersion is null || setupVersion != release.Version || release.Version <= installedVersion)
                return PrintBridgeSetupVerification.VersionMismatch;

            return PrintBridgeSetupVerification.Verified;
        }
    }

    /// <summary>The update failure a verification outcome stands for.</summary>
    public static PrintBridgeUpdateFailure ToFailure(PrintBridgeSetupVerification outcome) => outcome switch
    {
        PrintBridgeSetupVerification.SizeMismatch => PrintBridgeUpdateFailure.SizeMismatch,
        PrintBridgeSetupVerification.HashMismatch => PrintBridgeUpdateFailure.HashMismatch,
        PrintBridgeSetupVerification.NotSigned => PrintBridgeUpdateFailure.NotSigned,
        PrintBridgeSetupVerification.SignatureInvalid => PrintBridgeUpdateFailure.SignatureInvalid,
        PrintBridgeSetupVerification.PublisherMismatch => PrintBridgeUpdateFailure.PublisherMismatch,
        PrintBridgeSetupVerification.VersionMismatch => PrintBridgeUpdateFailure.VersionMismatch,
        _ => PrintBridgeUpdateFailure.Storage
    };
}
