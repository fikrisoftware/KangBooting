using DiscUtils;

namespace KangBooting.Core;

public interface IIsoInspector
{
    Task<IsoAnalysis> AnalyzeAsync(string isoPath, CancellationToken ct = default);
}

public class IsoInspector : IIsoInspector
{
    public Task<IsoAnalysis> AnalyzeAsync(string isoPath, CancellationToken ct = default)
    {
        using var fs = File.OpenRead(isoPath);
        using var reader = IsoFileSystemOpener.Open(fs);

        long? installImageSize = TryGetFileSize(reader, @"sources\install.wim")
            ?? TryGetFileSize(reader, @"sources\install.esd");

        // NOTE: etfsboot.com lives in the ISO9660 "El Torito" boot catalog area, which
        // is part of the classic ISO9660 layer even on discs whose real file content is
        // UDF (see IsoFileSystemOpener). On such discs this check will not find it via
        // the UDF reader, so HasBiosBootSector may read false even for a disc that does
        // support BIOS boot — this only affects BootModeRecommender's suggested default,
        // not correctness of an explicitly-chosen write mode (users can still override).
        bool hasBiosBoot = reader.FileExists(@"boot\etfsboot.com")
            || reader.FileExists(@"boot.bin");

        bool hasUefiBoot = reader.FileExists(@"efi\boot\bootx64.efi");

        var analysis = new IsoAnalysis(installImageSize, hasBiosBoot, hasUefiBoot);
        return Task.FromResult(analysis);
    }

    private static long? TryGetFileSize(IFileSystem reader, string path)
    {
        if (!reader.FileExists(path))
        {
            return null;
        }

        return reader.GetFileInfo(path).Length;
    }
}
