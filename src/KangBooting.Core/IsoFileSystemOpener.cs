using DiscUtils.Iso9660;
using DiscUtils.Udf;
using DiscUtils.Vfs;

namespace KangBooting.Core;

// Root-cause fix (confirmed against a real Windows 11 25H2 ISO): many modern Windows
// installation ISOs are UDF-formatted, with only a minimal ISO9660 "bridge" tree —
// present purely for BIOS/El Torito boot-catalog compatibility — that in practice
// contains almost nothing. Empirically, for the real ISO this was diagnosed against,
// DiscUtils.Iso9660.CDReader (joliet: true) saw exactly one 135-byte README.TXT and
// zero directories at the root, while DiscUtils.Udf.UdfReader on the same file correctly
// saw the full sources\/boot\/efi\/support\ tree. Reading via CDReader alone silently
// "succeeds" at copying almost nothing — no exception, no error, just a near-empty
// result — which is exactly what a user reported ("100% done, but only readme.txt on
// the USB").
//
// Fix: try UDF first (where real content lives on these discs); fall back to
// ISO9660/Joliet for discs that are genuinely ISO9660-only — including this project's
// own synthetic test fixtures (built via DiscUtils.Iso9660.CDBuilder, which produce
// ISO9660+Joliet only, no UDF), for which UdfReader reliably throws
// InvalidDataException("Stream is not a recognized UDF format"), verified empirically.
internal static class IsoFileSystemOpener
{
    public static VfsFileSystemFacade Open(Stream isoStream)
    {
        try
        {
            return new UdfReader(isoStream);
        }
        catch (InvalidDataException)
        {
            isoStream.Position = 0;
            return new CDReader(isoStream, joliet: true);
        }
    }
}
