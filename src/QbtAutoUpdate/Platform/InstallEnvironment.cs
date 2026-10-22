using System.Diagnostics;
using Microsoft.Win32;
using QbtAutoUpdate.Core;

namespace QbtAutoUpdate.Platform;

public enum PeMachine
{
    Unknown,
    I386,
    Amd64,
    Arm64,
}

/// <summary>Version resource and PE header of an executable.</summary>
public sealed record ExeMetadata(QbtVersion? FileVersion, string? CompanyName, string? ProductName, PeMachine Machine);

/// <summary>
/// Everything <see cref="InstallDetector"/> reads from the machine, behind one interface so the
/// detection rules can be tested without a real installation.
/// </summary>
public interface IInstallEnvironment
{
    string? GetMachineString(RegistryView view, string subKey, string valueName);
    bool FileExists(string path);
    bool DirectoryExists(string path);
    ExeMetadata GetExeMetadata(string path);
    BinaryScanResult ScanExe(string path);
    IReadOnlyList<string> RunningQbittorrentPaths();
    string ProgramFilesX86 { get; }
}

public sealed class SystemInstallEnvironment : IInstallEnvironment
{
    public string ProgramFilesX86 { get; } = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    public string? GetMachineString(RegistryView view, string subKey, string valueName)
    {
        using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using RegistryKey? key = root.OpenSubKey(subKey);
        return key?.GetValue(valueName) as string;
    }

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public ExeMetadata GetExeMetadata(string path)
    {
        FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
        // Use the numeric fields: qbittorrent.exe's FileVersion string is "v5.3.0".
        QbtVersion? version = info.FileMajorPart == 0 && info.FileMinorPart == 0
            ? null
            : new QbtVersion(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        return new ExeMetadata(version, info.CompanyName?.Trim(), info.ProductName?.Trim(), ReadMachine(path));
    }

    public BinaryScanResult ScanExe(string path) => QbtBinaryScanner.Scan(path);

    public IReadOnlyList<string> RunningQbittorrentPaths() =>
        ProcessTools.FindQbittorrent().Select(p => p.Path).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    internal static PeMachine ReadMachine(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D) // "MZ"
                return PeMachine.Unknown;
            stream.Position = 0x3C;
            int peOffset = reader.ReadInt32();
            if (peOffset <= 0 || peOffset > stream.Length - 6)
                return PeMachine.Unknown;
            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) // "PE\0\0"
                return PeMachine.Unknown;
            return reader.ReadUInt16() switch
            {
                NativeMethods.IMAGE_FILE_MACHINE_I386 => PeMachine.I386,
                NativeMethods.IMAGE_FILE_MACHINE_AMD64 => PeMachine.Amd64,
                NativeMethods.IMAGE_FILE_MACHINE_ARM64 => PeMachine.Arm64,
                _ => PeMachine.Unknown,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PeMachine.Unknown;
        }
    }
}
