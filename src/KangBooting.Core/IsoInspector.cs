using DiscUtils.Iso9660;

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
        using var cdReader = new CDReader(fs, joliet: true);

        long? installImageSize = TryGetFileSize(cdReader, @"sources\install.wim")
            ?? TryGetFileSize(cdReader, @"sources\install.esd");

        bool hasBiosBoot = cdReader.FileExists(@"boot\etfsboot.com")
            || cdReader.FileExists(@"boot.bin");

        bool hasUefiBoot = cdReader.FileExists(@"efi\boot\bootx64.efi");

        var analysis = new IsoAnalysis(installImageSize, hasBiosBoot, hasUefiBoot);
        return Task.FromResult(analysis);
    }

    private static long? TryGetFileSize(CDReader reader, string path)
    {
        if (!reader.FileExists(path))
        {
            return null;
        }

        return reader.GetFileInfo(path).Length;
    }
}
