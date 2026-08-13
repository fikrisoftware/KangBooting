# KangBooting Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Windows desktop app (KangBooting) that flashes a single ISO onto a USB drive, correctly handling ISOs containing files >4GB (e.g. Windows install.wim), via two selectable boot modes: UEFI:NTFS or Legacy+Split-FAT32.

**Architecture:** WinUI3 (.NET 8) MVVM app. UI → ViewModels → Services. Services: `DriveService` (USB enumeration/locking via WMI + Win32 P/Invoke), `IsoInspector` (DiscUtils-based ISO analysis), `BootModeRecommender` (pure logic), `WriteEngine` (strategy pattern: `UefiNtfsWriter` / `LegacySplitWriter`), `ChecksumService` (SHA256), `ProgressReporter` (`IProgress<WriteProgress>`).

**Tech Stack:** C# / .NET 8, WinUI3, DiscUtils (NuGet: `DiscUtils.Complete`), Win32 P/Invoke (`DeviceIoControl`, WMI via `System.Management`), `dism.exe` (shell-out via `System.Diagnostics.Process`), xUnit for tests.

## Global Constraints

- Windows only for phase 1 (per spec). All disk access code lives behind service interfaces to ease future cross-platform porting — do not leak Win32 types into ViewModels.
- FAT32 4GB file-size limit is the core problem being solved; no code path may silently truncate or fail to write a file >4GB without either splitting it (Legacy mode) or using NTFS (UEFI mode).
- No silent failure: every error surfaced to the user must be a human-readable message, not a raw stack trace or native error code (per spec Error Handling section).
- Multi-ISO persistence, cross-platform support, and custom partition schemes are OUT OF SCOPE for this plan (spec: "Di Luar Scope Fase 1").

---

### Task 1: Solution scaffold + BootModeRecommender (pure logic, TDD)

**Files:**
- Create: `KangBooting.sln`
- Create: `src/KangBooting.Core/KangBooting.Core.csproj` (class library, .NET 8, no WinUI dependency — holds all testable logic)
- Create: `src/KangBooting.Core/IsoAnalysis.cs`
- Create: `src/KangBooting.Core/BootModeRecommender.cs`
- Test: `tests/KangBooting.Core.Tests/KangBooting.Core.Tests.csproj` (xUnit)
- Test: `tests/KangBooting.Core.Tests/BootModeRecommenderTests.cs`

**Interfaces:**
- Produces: `IsoAnalysis` record with properties `long? InstallImageSizeBytes`, `bool HasBiosBootSector`, `bool HasUefiBoot`.
- Produces: `enum BootMode { UefiNtfs, LegacySplitFat32 }`.
- Produces: `static class BootModeRecommender { public static BootMode Recommend(IsoAnalysis analysis) }`.

- [ ] **Step 1: Create solution and projects**

```bash
mkdir -p src/KangBooting.Core tests/KangBooting.Core.Tests
cd D:/Private/Iseng
dotnet new sln -n KangBooting
dotnet new classlib -n KangBooting.Core -o src/KangBooting.Core -f net8.0
dotnet new xunit -n KangBooting.Core.Tests -o tests/KangBooting.Core.Tests -f net8.0
dotnet sln add src/KangBooting.Core/KangBooting.Core.csproj
dotnet sln add tests/KangBooting.Core.Tests/KangBooting.Core.Tests.csproj
dotnet add tests/KangBooting.Core.Tests/KangBooting.Core.Tests.csproj reference src/KangBooting.Core/KangBooting.Core.csproj
```

Expected: `dotnet build` succeeds with no errors.

- [ ] **Step 2: Write IsoAnalysis and BootMode types**

`src/KangBooting.Core/IsoAnalysis.cs`:
```csharp
namespace KangBooting.Core;

public record IsoAnalysis(
    long? InstallImageSizeBytes,
    bool HasBiosBootSector,
    bool HasUefiBoot);

public enum BootMode
{
    UefiNtfs,
    LegacySplitFat32
}
```

- [ ] **Step 3: Write the failing tests for BootModeRecommender**

`tests/KangBooting.Core.Tests/BootModeRecommenderTests.cs`:
```csharp
using KangBooting.Core;
using Xunit;

namespace KangBooting.Core.Tests;

public class BootModeRecommenderTests
{
    private const long FourGb = 4L * 1024 * 1024 * 1024;

    [Fact]
    public void LargeImage_NoBiosBoot_RecommendsUefiNtfs()
    {
        var analysis = new IsoAnalysis(
            InstallImageSizeBytes: FourGb + 1,
            HasBiosBootSector: false,
            HasUefiBoot: true);

        var result = BootModeRecommender.Recommend(analysis);

        Assert.Equal(BootMode.UefiNtfs, result);
    }

    [Fact]
    public void LargeImage_WithBiosBoot_RecommendsLegacySplit()
    {
        var analysis = new IsoAnalysis(
            InstallImageSizeBytes: FourGb + 1,
            HasBiosBootSector: true,
            HasUefiBoot: true);

        var result = BootModeRecommender.Recommend(analysis);

        Assert.Equal(BootMode.LegacySplitFat32, result);
    }

    [Fact]
    public void NoLargeFile_DefaultsToUefiNtfs()
    {
        var analysis = new IsoAnalysis(
            InstallImageSizeBytes: 2L * 1024 * 1024 * 1024,
            HasBiosBootSector: true,
            HasUefiBoot: true);

        var result = BootModeRecommender.Recommend(analysis);

        Assert.Equal(BootMode.UefiNtfs, result);
    }

    [Fact]
    public void NoInstallImageAtAll_DefaultsToUefiNtfs()
    {
        var analysis = new IsoAnalysis(
            InstallImageSizeBytes: null,
            HasBiosBootSector: true,
            HasUefiBoot: true);

        var result = BootModeRecommender.Recommend(analysis);

        Assert.Equal(BootMode.UefiNtfs, result);
    }
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test tests/KangBooting.Core.Tests/`
Expected: FAIL — `BootModeRecommender` does not exist yet (compile error).

- [ ] **Step 5: Implement BootModeRecommender**

`src/KangBooting.Core/BootModeRecommender.cs`:
```csharp
namespace KangBooting.Core;

public static class BootModeRecommender
{
    private const long FourGigabytes = 4L * 1024 * 1024 * 1024;

    public static BootMode Recommend(IsoAnalysis analysis)
    {
        bool hasLargeFile = analysis.InstallImageSizeBytes is > FourGigabytes;

        if (hasLargeFile && analysis.HasBiosBootSector)
        {
            return BootMode.LegacySplitFat32;
        }

        return BootMode.UefiNtfs;
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/KangBooting.Core.Tests/`
Expected: PASS (4 tests).

- [ ] **Step 7: Commit**

```bash
git init
git add .
git commit -m "feat: scaffold solution, add BootModeRecommender with tests"
```

---

### Task 2: ChecksumService (TDD)

**Files:**
- Create: `src/KangBooting.Core/ChecksumService.cs`
- Test: `tests/KangBooting.Core.Tests/ChecksumServiceTests.cs`

**Interfaces:**
- Consumes: nothing from prior tasks.
- Produces: `interface IChecksumService { Task<string> ComputeSha256Async(Stream stream, CancellationToken ct = default); bool Matches(string hashA, string hashB); }` and its implementation `class ChecksumService : IChecksumService`.

- [ ] **Step 1: Write the failing tests**

`tests/KangBooting.Core.Tests/ChecksumServiceTests.cs`:
```csharp
using System.Text;
using KangBooting.Core;
using Xunit;

namespace KangBooting.Core.Tests;

public class ChecksumServiceTests
{
    [Fact]
    public async Task ComputeSha256Async_KnownInput_ReturnsKnownHash()
    {
        var service = new ChecksumService();
        var bytes = Encoding.UTF8.GetBytes("hello world");
        using var stream = new MemoryStream(bytes);

        var hash = await service.ComputeSha256Async(stream);

        // Precomputed SHA256 of "hello world"
        Assert.Equal(
            "b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde",
            hash);
    }

    [Fact]
    public void Matches_IsCaseInsensitive()
    {
        var service = new ChecksumService();

        Assert.True(service.Matches(
            "B94D27B9934D3E08A52E52D7DA7DABFAC484EFE37A5380EE9088F7ACE2EFCDE",
            "b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde"));
    }

    [Fact]
    public void Matches_DifferentHashes_ReturnsFalse()
    {
        var service = new ChecksumService();

        Assert.False(service.Matches("abc123", "def456"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/KangBooting.Core.Tests/`
Expected: FAIL — `ChecksumService` does not exist.

- [ ] **Step 3: Implement ChecksumService**

`src/KangBooting.Core/ChecksumService.cs`:
```csharp
using System.Security.Cryptography;

namespace KangBooting.Core;

public interface IChecksumService
{
    Task<string> ComputeSha256Async(Stream stream, CancellationToken ct = default);
    bool Matches(string hashA, string hashB);
}

public class ChecksumService : IChecksumService
{
    public async Task<string> ComputeSha256Async(Stream stream, CancellationToken ct = default)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = await sha256.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public bool Matches(string hashA, string hashB)
    {
        return string.Equals(hashA, hashB, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/KangBooting.Core.Tests/`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/KangBooting.Core/ChecksumService.cs tests/KangBooting.Core.Tests/ChecksumServiceTests.cs
git commit -m "feat: add ChecksumService with SHA256 verification"
```

---

### Task 3: IsoInspector (DiscUtils-based ISO analysis)

**Files:**
- Modify: `src/KangBooting.Core/KangBooting.Core.csproj` (add DiscUtils.Complete NuGet package)
- Create: `src/KangBooting.Core/IsoInspector.cs`
- Test: `tests/KangBooting.Core.Tests/IsoInspectorTests.cs`
- Test fixture: `tests/KangBooting.Core.Tests/Fixtures/build-test-iso.md` (documents how the test ISO fixture was built, since binary ISOs aren't hand-written)

**Interfaces:**
- Consumes: `IsoAnalysis` (Task 1).
- Produces: `interface IIsoInspector { Task<IsoAnalysis> AnalyzeAsync(string isoPath, CancellationToken ct = default); }` and `class IsoInspector : IIsoInspector`.

- [ ] **Step 1: Add DiscUtils package**

```bash
dotnet add src/KangBooting.Core/KangBooting.Core.csproj package DiscUtils.Complete
dotnet add tests/KangBooting.Core.Tests/KangBooting.Core.Tests.csproj package DiscUtils.Complete
```

Expected: `dotnet restore` succeeds, package appears in `KangBooting.Core.csproj`.

- [ ] **Step 2: Build a minimal test ISO fixture**

Create `tests/KangBooting.Core.Tests/Fixtures/build-test-iso.md` documenting the fixture (since we generate it programmatically in the test's `ClassFixture`, not a checked-in binary):

```markdown
# Test ISO Fixture

`IsoInspectorTests` builds a synthetic ISO in-memory using `DiscUtils.Iso9660.CDBuilder`
at test setup time rather than shipping a binary .iso file. This keeps the repo
free of large binary fixtures and makes the exact byte layout (file sizes, presence
of boot files) explicit and easy to vary per test case.
```

- [ ] **Step 3: Write the failing tests**

`tests/KangBooting.Core.Tests/IsoInspectorTests.cs`:
```csharp
using DiscUtils.Iso9660;
using KangBooting.Core;
using Xunit;

namespace KangBooting.Core.Tests;

public class IsoInspectorTests : IDisposable
{
    private readonly string _tempDir;

    public IsoInspectorTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("kangbooting-tests").FullName;
    }

    public void Dispose()
    {
        Directory.Delete(_tempDir, recursive: true);
    }

    private string BuildIso(bool includeBiosBoot, bool includeUefiBoot, int installWimSizeMb)
    {
        var builder = new CDBuilder { UseJoliet = true };

        var wimBytes = new byte[installWimSizeMb * 1024 * 1024];
        builder.AddFile(@"sources\install.wim", wimBytes);

        if (includeBiosBoot)
        {
            builder.AddFile(@"boot\etfsboot.com", new byte[512]);
        }

        if (includeUefiBoot)
        {
            builder.AddFile(@"efi\boot\bootx64.efi", new byte[1024]);
        }

        var isoPath = Path.Combine(_tempDir, $"test-{Guid.NewGuid():N}.iso");
        using (var fs = File.Create(isoPath))
        {
            builder.Build(fs);
        }

        return isoPath;
    }

    [Fact]
    public async Task AnalyzeAsync_DetectsLargeInstallWim()
    {
        var isoPath = BuildIso(includeBiosBoot: false, includeUefiBoot: true, installWimSizeMb: 10);
        var inspector = new IsoInspector();

        var result = await inspector.AnalyzeAsync(isoPath);

        Assert.NotNull(result.InstallImageSizeBytes);
        Assert.Equal(10 * 1024 * 1024, result.InstallImageSizeBytes);
    }

    [Fact]
    public async Task AnalyzeAsync_DetectsBiosBootSector()
    {
        var isoPath = BuildIso(includeBiosBoot: true, includeUefiBoot: true, installWimSizeMb: 1);
        var inspector = new IsoInspector();

        var result = await inspector.AnalyzeAsync(isoPath);

        Assert.True(result.HasBiosBootSector);
        Assert.True(result.HasUefiBoot);
    }

    [Fact]
    public async Task AnalyzeAsync_NoBiosBootSector_ReportsFalse()
    {
        var isoPath = BuildIso(includeBiosBoot: false, includeUefiBoot: true, installWimSizeMb: 1);
        var inspector = new IsoInspector();

        var result = await inspector.AnalyzeAsync(isoPath);

        Assert.False(result.HasBiosBootSector);
    }
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test tests/KangBooting.Core.Tests/`
Expected: FAIL — `IsoInspector` does not exist.

- [ ] **Step 5: Implement IsoInspector**

`src/KangBooting.Core/IsoInspector.cs`:
```csharp
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
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/KangBooting.Core.Tests/`
Expected: PASS (3 tests).

- [ ] **Step 7: Commit**

```bash
git add src/KangBooting.Core/IsoInspector.cs tests/KangBooting.Core.Tests/IsoInspectorTests.cs tests/KangBooting.Core.Tests/Fixtures/build-test-iso.md src/KangBooting.Core/KangBooting.Core.csproj tests/KangBooting.Core.Tests/KangBooting.Core.Tests.csproj
git commit -m "feat: add IsoInspector using DiscUtils for ISO analysis"
```

---

### Task 4: DriveService (USB enumeration + volume locking)

**Files:**
- Create: `src/KangBooting.Core/DriveInfo.cs`
- Create: `src/KangBooting.Core/DriveService.cs`
- Create: `src/KangBooting.Core/NativeMethods.cs` (P/Invoke declarations, isolated in one file so unsafe/native code doesn't spread)
- Test: `tests/KangBooting.Core.Tests/DriveServiceTests.cs`

**Interfaces:**
- Produces: `record UsbDriveInfo(string DeviceId, string DisplayName, long SizeBytes, string CurrentFileSystem)`.
- Produces: `interface IDriveService { IReadOnlyList<UsbDriveInfo> EnumerateUsbDrives(); IDisposable LockVolume(string deviceId); }`. `LockVolume` returns an `IDisposable` whose `Dispose()` releases the lock — callers use it in a `using` block so a lock can never be left held on an exception path.
- Produces (in `NativeMethods.cs`): `internal static class NativeMethods` wrapping `CreateFile`, `DeviceIoControl`, `CloseHandle` and the `FSCTL_LOCK_VOLUME` control code.

**Note on testability:** WMI enumeration and real volume locking require actual hardware/OS and can't run in a normal CI unit test. This task splits `DriveService` so the WMI query is isolated behind the interface — tests here cover `UsbDriveInfo` construction logic and mark the hardware-dependent parts for manual verification (see Task 9).

- [ ] **Step 1: Write UsbDriveInfo and the interface**

`src/KangBooting.Core/DriveInfo.cs`:
```csharp
namespace KangBooting.Core;

public record UsbDriveInfo(
    string DeviceId,
    string DisplayName,
    long SizeBytes,
    string CurrentFileSystem);
```

- [ ] **Step 2: Write NativeMethods (P/Invoke)**

`src/KangBooting.Core/NativeMethods.cs`:
```csharp
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KangBooting.Core;

internal static class NativeMethods
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint OPEN_EXISTING = 3;
    public const uint FSCTL_LOCK_VOLUME = 0x00090018;
    public const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}
```

- [ ] **Step 3: Write DriveService**

`src/KangBooting.Core/DriveService.cs`:
```csharp
using System.Management;
using Microsoft.Win32.SafeHandles;

namespace KangBooting.Core;

public interface IDriveService
{
    IReadOnlyList<UsbDriveInfo> EnumerateUsbDrives();
    IDisposable LockVolume(string deviceId);
}

public class DriveService : IDriveService
{
    public IReadOnlyList<UsbDriveInfo> EnumerateUsbDrives()
    {
        var drives = new List<UsbDriveInfo>();

        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, Caption, Size, InterfaceType FROM Win32_DiskDrive WHERE InterfaceType='USB'");

        foreach (ManagementObject drive in searcher.Get())
        {
            var deviceId = (string)drive["DeviceID"];
            var caption = (string)drive["Caption"];
            var size = drive["Size"] is not null ? Convert.ToInt64(drive["Size"]) : 0L;

            drives.Add(new UsbDriveInfo(
                DeviceId: deviceId,
                DisplayName: caption,
                SizeBytes: size,
                CurrentFileSystem: GetFileSystem(deviceId)));
        }

        return drives;
    }

    private static string GetFileSystem(string deviceId)
    {
        // Partition/logical-disk association query kept separate so it can fail
        // independently without aborting the whole drive listing.
        using var searcher = new ManagementObjectSearcher(
            $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{deviceId.Replace(@"\", @"\\")}'}} " +
            "WHERE AssocClass = Win32_DiskDriveToDiskPartition");

        foreach (ManagementObject partition in searcher.Get())
        {
            using var logicalSearcher = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} " +
                "WHERE AssocClass = Win32_LogicalDiskToPartition");

            foreach (ManagementObject logicalDisk in logicalSearcher.Get())
            {
                return (string?)logicalDisk["FileSystem"] ?? "Unknown";
            }
        }

        return "Unknown";
    }

    public IDisposable LockVolume(string deviceId)
    {
        var handle = NativeMethods.CreateFile(
            deviceId,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new IOException($"Tidak bisa membuka drive {deviceId}. Drive mungkin sedang dipakai aplikasi lain.");
        }

        bool locked = NativeMethods.DeviceIoControl(
            handle, NativeMethods.FSCTL_LOCK_VOLUME,
            IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

        if (!locked)
        {
            handle.Dispose();
            throw new IOException($"Drive {deviceId} sedang digunakan aplikasi lain, tutup dulu aplikasi yang mengakses drive tersebut.");
        }

        return new VolumeLock(handle);
    }

    private sealed class VolumeLock : IDisposable
    {
        private readonly SafeFileHandle _handle;

        public VolumeLock(SafeFileHandle handle)
        {
            _handle = handle;
        }

        public void Dispose()
        {
            _handle.Dispose();
        }
    }
}
```

- [ ] **Step 4: Write a test for the parts that don't need hardware**

`tests/KangBooting.Core.Tests/DriveServiceTests.cs`:
```csharp
using KangBooting.Core;
using Xunit;

namespace KangBooting.Core.Tests;

public class DriveServiceTests
{
    [Fact]
    public void UsbDriveInfo_StoresAllFields()
    {
        var info = new UsbDriveInfo(
            DeviceId: @"\\.\PHYSICALDRIVE1",
            DisplayName: "SanDisk USB Device",
            SizeBytes: 32L * 1024 * 1024 * 1024,
            CurrentFileSystem: "FAT32");

        Assert.Equal(@"\\.\PHYSICALDRIVE1", info.DeviceId);
        Assert.Equal(32L * 1024 * 1024 * 1024, info.SizeBytes);
        Assert.Equal("FAT32", info.CurrentFileSystem);
    }

    // EnumerateUsbDrives() and LockVolume() require real USB hardware and Windows
    // WMI/kernel access — they are covered by manual hardware testing (see Task 9
    // of the implementation plan), not by this automated suite.
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/KangBooting.Core.Tests/`
Expected: PASS (all tests so far, including the new one).

- [ ] **Step 6: Commit**

```bash
git add src/KangBooting.Core/DriveInfo.cs src/KangBooting.Core/DriveService.cs src/KangBooting.Core/NativeMethods.cs tests/KangBooting.Core.Tests/DriveServiceTests.cs
git commit -m "feat: add DriveService for USB enumeration and volume locking"
```

---

### Task 5: ProgressReporter model

**Files:**
- Create: `src/KangBooting.Core/WriteProgress.cs`

**Interfaces:**
- Produces: `record WriteProgress(double PercentComplete, double BytesPerSecond, TimeSpan? EstimatedTimeRemaining, string CurrentOperation)`. Consumed via `IProgress<WriteProgress>` by `WriteEngine` implementations (Tasks 6–7) and the UI (Task 8).

- [ ] **Step 1: Write WriteProgress record**

`src/KangBooting.Core/WriteProgress.cs`:
```csharp
namespace KangBooting.Core;

public record WriteProgress(
    double PercentComplete,
    double BytesPerSecond,
    TimeSpan? EstimatedTimeRemaining,
    string CurrentOperation);
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/KangBooting.Core/`
Expected: Build succeeds, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/KangBooting.Core/WriteProgress.cs
git commit -m "feat: add WriteProgress model for progress reporting"
```

---

### Task 6: WriteEngine — UefiNtfsWriter

**Files:**
- Create: `src/KangBooting.Core/IWriteEngine.cs`
- Create: `src/KangBooting.Core/UefiNtfsWriter.cs`
- Create: `assets/uefi-ntfs.img` (bundled UEFI:NTFS bootloader binary from the akeo/rufus project, MIT license — placed here as a build asset)
- Modify: `src/KangBooting.Core/KangBooting.Core.csproj` (embed `assets/uefi-ntfs.img` as a copy-to-output content item)
- Test: `tests/KangBooting.Core.Tests/UefiNtfsWriterTests.cs`

**Interfaces:**
- Consumes: `UsbDriveInfo` (Task 4), `WriteProgress` (Task 5), `IIsoInspector`/ISO path.
- Produces: `interface IWriteEngine { Task WriteAsync(string isoPath, UsbDriveInfo target, IProgress<WriteProgress> progress, CancellationToken ct = default); }` implemented by `UefiNtfsWriter`.

**Note:** Real partitioning/formatting requires a real disk handle and is not safely unit-testable (writing to `\\.\PHYSICALDRIVEn` in a test would destroy a real disk). This task isolates the ISO-copy logic (testable against a virtual/in-memory NTFS target via DiscUtils) from the raw partitioning step (manually verified per Task 9), matching the spec's own testing strategy (VHD/virtual disk where possible, physical hardware for final validation).

- [ ] **Step 1: Write the IWriteEngine interface**

`src/KangBooting.Core/IWriteEngine.cs`:
```csharp
namespace KangBooting.Core;

public interface IWriteEngine
{
    Task WriteAsync(
        string isoPath,
        UsbDriveInfo target,
        IProgress<WriteProgress> progress,
        CancellationToken ct = default);
}
```

- [ ] **Step 2: Obtain the UEFI:NTFS bootloader asset**

Download the UEFI:NTFS image from the akeo.freeware/rufus project releases (MIT license) and place it at `assets/uefi-ntfs.img`. Document its source and license in `assets/README.md`:

```markdown
# assets/uefi-ntfs.img

Source: akeo.freeware "uefi-ntfs" project (https://github.com/pbatard/uefi-ntfs), MIT License.
Used unmodified as the small FAT32 boot partition payload for UEFI:NTFS mode,
so a UEFI firmware that cannot read NTFS natively can chain-load into the NTFS
partition containing the actual Windows installer files.
```

- [ ] **Step 3: Write the failing test for the copy logic (against a virtual NTFS stream)**

`tests/KangBooting.Core.Tests/UefiNtfsWriterTests.cs`:
```csharp
using DiscUtils.Ntfs;
using DiscUtils.Iso9660;
using DiscUtils.Streams;
using KangBooting.Core;
using Xunit;

namespace KangBooting.Core.Tests;

public class UefiNtfsWriterTests : IDisposable
{
    private readonly string _tempDir;

    public UefiNtfsWriterTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("kangbooting-uefi-tests").FullName;
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string BuildIsoWithFile(string relativePath, int sizeMb)
    {
        var builder = new CDBuilder { UseJoliet = true };
        builder.AddFile(relativePath, new byte[sizeMb * 1024 * 1024]);

        var isoPath = Path.Combine(_tempDir, $"test-{Guid.NewGuid():N}.iso");
        using var fs = File.Create(isoPath);
        builder.Build(fs);
        return isoPath;
    }

    [Fact]
    public void CopyIsoContentsToNtfs_PreservesLargeFileWithoutSplitting()
    {
        // Arrange: an ISO with a file bigger than the FAT32 4GB limit would allow,
        // written to an in-memory NTFS volume to prove NTFS handles it as one file.
        var isoPath = BuildIsoWithFile(@"sources\install.wim", sizeMb: 10);

        using var isoStream = File.OpenRead(isoPath);
        using var cdReader = new CDReader(isoStream, joliet: true);

        var ntfsStream = new SparseMemoryStream();
        NtfsFileSystem.Format(ntfsStream, "TESTVOL", new DiscUtils.Geometry(1, 1, 1), 0, 200 * 1024 * 1024 / 512);
        using var ntfs = new NtfsFileSystem(ntfsStream);

        // Act
        UefiNtfsWriter.CopyIsoContentsToFileSystem(cdReader, ntfs);

        // Assert: the file exists on the NTFS volume as a single, unsplit file.
        Assert.True(ntfs.FileExists(@"sources\install.wim"));
        Assert.Equal(10 * 1024 * 1024, ntfs.GetFileLength(@"sources\install.wim"));
    }
}
```

- [ ] **Step 4: Run test to verify it fails**

Run: `dotnet test tests/KangBooting.Core.Tests/`
Expected: FAIL — `UefiNtfsWriter.CopyIsoContentsToFileSystem` does not exist.

- [ ] **Step 5: Implement UefiNtfsWriter**

`src/KangBooting.Core/UefiNtfsWriter.cs`:
```csharp
using DiscUtils;
using DiscUtils.Iso9660;

namespace KangBooting.Core;

public class UefiNtfsWriter : IWriteEngine
{
    private readonly IDriveService _driveService;
    private readonly IPartitioner _partitioner;

    public UefiNtfsWriter(IDriveService driveService, IPartitioner partitioner)
    {
        _driveService = driveService;
        _partitioner = partitioner;
    }

    public async Task WriteAsync(
        string isoPath,
        UsbDriveInfo target,
        IProgress<WriteProgress> progress,
        CancellationToken ct = default)
    {
        progress.Report(new WriteProgress(0, 0, null, "Formatting"));

        using var isoStream = File.OpenRead(isoPath);
        using var cdReader = new CDReader(isoStream, joliet: true);

        using (var volumeLock = _driveService.LockVolume(target.DeviceId))
        {
            var (bootPartition, dataPartition) = await _partitioner
                .CreateUefiNtfsLayoutAsync(target, ct);

            await _partitioner.WriteBootloaderImageAsync(
                bootPartition, "assets/uefi-ntfs.img", ct);

            progress.Report(new WriteProgress(10, 0, null, "Copying files"));

            using var ntfs = _partitioner.OpenNtfsFileSystem(dataPartition);
            CopyIsoContentsToFileSystem(cdReader, ntfs, progress);
        }

        progress.Report(new WriteProgress(100, 0, TimeSpan.Zero, "Selesai"));
    }

    internal static void CopyIsoContentsToFileSystem(
        CDReader source,
        IFileSystem destination,
        IProgress<WriteProgress>? progress = null)
    {
        CopyDirectory(source, destination, "", progress);
    }

    private static void CopyDirectory(
        CDReader source,
        IFileSystem destination,
        string path,
        IProgress<WriteProgress>? progress)
    {
        foreach (var dir in source.GetDirectories(path))
        {
            destination.CreateDirectory(dir);
            CopyDirectory(source, destination, dir, progress);
        }

        foreach (var file in source.GetFiles(path))
        {
            using var sourceStream = source.OpenFile(file, FileMode.Open);
            using var destStream = destination.OpenFile(file, FileMode.Create, FileAccess.Write);
            sourceStream.CopyTo(destStream);
        }
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/KangBooting.Core.Tests/`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/KangBooting.Core/IWriteEngine.cs src/KangBooting.Core/UefiNtfsWriter.cs assets/ tests/KangBooting.Core.Tests/UefiNtfsWriterTests.cs src/KangBooting.Core/KangBooting.Core.csproj
git commit -m "feat: add UefiNtfsWriter with tested ISO-to-NTFS copy logic"
```

---

### Task 7: WriteEngine — LegacySplitWriter (dism.exe split integration)

**Files:**
- Create: `src/KangBooting.Core/DismRunner.cs`
- Create: `src/KangBooting.Core/LegacySplitWriter.cs`
- Test: `tests/KangBooting.Core.Tests/DismRunnerTests.cs`

**Interfaces:**
- Consumes: `IWriteEngine` (Task 6), `WriteProgress` (Task 5), `UsbDriveInfo` (Task 4).
- Produces: `interface IDismRunner { Task SplitWimAsync(string wimPath, string outputSwmPath, int maxSizeMb, CancellationToken ct = default); }` implemented by `DismRunner`. Produces `class LegacySplitWriter : IWriteEngine`.

- [ ] **Step 1: Write the failing test for DismRunner's argument building and error parsing**

`tests/KangBooting.Core.Tests/DismRunnerTests.cs`:
```csharp
using KangBooting.Core;
using Xunit;

namespace KangBooting.Core.Tests;

public class DismRunnerTests
{
    [Fact]
    public void BuildSplitArguments_ProducesExpectedCommandLine()
    {
        var args = DismRunner.BuildSplitArguments(
            wimPath: @"D:\staging\sources\install.wim",
            outputSwmPath: @"D:\staging\sources\install.swm",
            maxSizeMb: 4000);

        Assert.Equal(
            "/Split-Image /ImageFile:\"D:\\staging\\sources\\install.wim\" " +
            "/SWMFile:\"D:\\staging\\sources\\install.swm\" /FileSize:4000",
            args);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(87, false)]
    [InlineData(-2147024784, false)]
    public void IsSuccessExitCode_OnlyZeroIsSuccess(int exitCode, bool expectedSuccess)
    {
        Assert.Equal(expectedSuccess, DismRunner.IsSuccessExitCode(exitCode));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/KangBooting.Core.Tests/`
Expected: FAIL — `DismRunner` does not exist.

- [ ] **Step 3: Implement DismRunner**

`src/KangBooting.Core/DismRunner.cs`:
```csharp
using System.Diagnostics;

namespace KangBooting.Core;

public interface IDismRunner
{
    Task SplitWimAsync(string wimPath, string outputSwmPath, int maxSizeMb, CancellationToken ct = default);
}

public class DismRunner : IDismRunner
{
    public async Task SplitWimAsync(string wimPath, string outputSwmPath, int maxSizeMb, CancellationToken ct = default)
    {
        var args = BuildSplitArguments(wimPath, outputSwmPath, maxSizeMb);

        var startInfo = new ProcessStartInfo
        {
            FileName = "dism.exe",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new IOException("Gagal menjalankan dism.exe. Pastikan Windows ADK/DISM tersedia di sistem.");

        string stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (!IsSuccessExitCode(process.ExitCode))
        {
            throw new IOException(
                $"Gagal memecah install.wim (dism.exe exit code {process.ExitCode}): " +
                (string.IsNullOrWhiteSpace(stderr) ? "Tidak ada detail error dari dism.exe." : stderr.Trim()));
        }

        File.Delete(wimPath);
    }

    internal static string BuildSplitArguments(string wimPath, string outputSwmPath, int maxSizeMb)
    {
        return $"/Split-Image /ImageFile:\"{wimPath}\" /SWMFile:\"{outputSwmPath}\" /FileSize:{maxSizeMb}";
    }

    internal static bool IsSuccessExitCode(int exitCode) => exitCode == 0;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/KangBooting.Core.Tests/`
Expected: PASS.

- [ ] **Step 5: Implement LegacySplitWriter (uses DismRunner + DiscUtils FAT32 copy)**

`src/KangBooting.Core/LegacySplitWriter.cs`:
```csharp
using DiscUtils;
using DiscUtils.Iso9660;

namespace KangBooting.Core;

public class LegacySplitWriter : IWriteEngine
{
    private readonly IDriveService _driveService;
    private readonly IPartitioner _partitioner;
    private readonly IDismRunner _dismRunner;

    private const int FourGigabytes = 4000; // MB, matches spec's split threshold

    public LegacySplitWriter(IDriveService driveService, IPartitioner partitioner, IDismRunner dismRunner)
    {
        _driveService = driveService;
        _partitioner = partitioner;
        _dismRunner = dismRunner;
    }

    public async Task WriteAsync(
        string isoPath,
        UsbDriveInfo target,
        IProgress<WriteProgress> progress,
        CancellationToken ct = default)
    {
        progress.Report(new WriteProgress(0, 0, null, "Formatting"));

        var stagingDir = Directory.CreateTempSubdirectory("kangbooting-staging").FullName;
        try
        {
            using (var isoStream = File.OpenRead(isoPath))
            using (var cdReader = new CDReader(isoStream, joliet: true))
            {
                progress.Report(new WriteProgress(10, 0, null, "Extracting ISO"));
                ExtractIsoToDirectory(cdReader, stagingDir);
            }

            var wimPath = Path.Combine(stagingDir, "sources", "install.wim");
            if (File.Exists(wimPath) && new FileInfo(wimPath).Length > FourGigabytes * 1024L * 1024)
            {
                progress.Report(new WriteProgress(50, 0, null, "Splitting install.wim"));
                var swmPath = Path.Combine(stagingDir, "sources", "install.swm");
                await _dismRunner.SplitWimAsync(wimPath, swmPath, FourGigabytes, ct);
            }

            using (var volumeLock = _driveService.LockVolume(target.DeviceId))
            {
                var fat32Partition = await _partitioner.CreateLegacyFat32LayoutAsync(target, ct);

                progress.Report(new WriteProgress(80, 0, null, "Copying files"));
                using var fat32 = _partitioner.OpenFat32FileSystem(fat32Partition);
                CopyDirectoryToFileSystem(stagingDir, fat32, "");
            }
        }
        finally
        {
            Directory.Delete(stagingDir, recursive: true);
        }

        progress.Report(new WriteProgress(100, 0, TimeSpan.Zero, "Selesai"));
    }

    private static void ExtractIsoToDirectory(CDReader source, string destinationDir)
    {
        foreach (var dir in source.GetDirectories(""))
        {
            Directory.CreateDirectory(Path.Combine(destinationDir, dir));
        }

        foreach (var file in source.GetFiles(""))
        {
            var destPath = Path.Combine(destinationDir, file);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            using var sourceStream = source.OpenFile(file, FileMode.Open);
            using var destStream = File.Create(destPath);
            sourceStream.CopyTo(destStream);
        }
    }

    private static void CopyDirectoryToFileSystem(string sourceDir, IFileSystem destination, string relativePath)
    {
        var fullSourceDir = Path.Combine(sourceDir, relativePath);

        foreach (var dir in Directory.GetDirectories(fullSourceDir))
        {
            var relDir = Path.GetRelativePath(sourceDir, dir);
            destination.CreateDirectory(relDir);
            CopyDirectoryToFileSystem(sourceDir, destination, relDir);
        }

        foreach (var file in Directory.GetFiles(fullSourceDir))
        {
            var relFile = Path.GetRelativePath(sourceDir, file);
            using var sourceStream = File.OpenRead(file);
            using var destStream = destination.OpenFile(relFile, FileMode.Create, FileAccess.Write);
            sourceStream.CopyTo(destStream);
        }
    }
}
```

- [ ] **Step 6: Build to verify it compiles**

Run: `dotnet build src/KangBooting.Core/`
Expected: Build succeeds. Note: `IPartitioner` is referenced here but not yet defined — Task 8 defines it. If the build fails on missing `IPartitioner`, proceed to Task 8's Step 1 first, then return here.

- [ ] **Step 7: Commit**

```bash
git add src/KangBooting.Core/DismRunner.cs src/KangBooting.Core/LegacySplitWriter.cs tests/KangBooting.Core.Tests/DismRunnerTests.cs
git commit -m "feat: add LegacySplitWriter with dism.exe-based install.wim splitting"
```

---

### Task 8: IPartitioner (raw disk partition/format operations)

**Files:**
- Create: `src/KangBooting.Core/IPartitioner.cs`
- Create: `src/KangBooting.Core/Partitioner.cs`

**Interfaces:**
- Consumes: `UsbDriveInfo` (Task 4).
- Produces: `interface IPartitioner` with methods used by Task 6/7:
  - `Task<(PartitionHandle bootPartition, PartitionHandle dataPartition)> CreateUefiNtfsLayoutAsync(UsbDriveInfo target, CancellationToken ct)`
  - `Task<PartitionHandle> CreateLegacyFat32LayoutAsync(UsbDriveInfo target, CancellationToken ct)`
  - `Task WriteBootloaderImageAsync(PartitionHandle partition, string imagePath, CancellationToken ct)`
  - `NtfsFileSystem OpenNtfsFileSystem(PartitionHandle partition)` (DiscUtils type)
  - `FatFileSystem OpenFat32FileSystem(PartitionHandle partition)` (DiscUtils type)
  - `record PartitionHandle(string DeviceId, int PartitionIndex)`

**Note:** This is the one component that genuinely cannot be unit-tested without risking real disk destruction — even a VHD-based test exercises DiscUtils' virtual disk APIs, not the real GPT/MBR partitioning IOCTLs (`IOCTL_DISK_SET_DRIVE_LAYOUT_EX` etc.) this class wraps. Per the spec's testing section, this is deferred to manual hardware validation (Task 9). The implementation below is written directly rather than test-first, since there is no safe automated test to write against real disk IOCTLs.

- [ ] **Step 1: Write the IPartitioner interface**

`src/KangBooting.Core/IPartitioner.cs`:
```csharp
using DiscUtils.Fat;
using DiscUtils.Ntfs;

namespace KangBooting.Core;

public record PartitionHandle(string DeviceId, int PartitionIndex);

public interface IPartitioner
{
    Task<(PartitionHandle bootPartition, PartitionHandle dataPartition)> CreateUefiNtfsLayoutAsync(
        UsbDriveInfo target, CancellationToken ct = default);

    Task<PartitionHandle> CreateLegacyFat32LayoutAsync(
        UsbDriveInfo target, CancellationToken ct = default);

    Task WriteBootloaderImageAsync(
        PartitionHandle partition, string imagePath, CancellationToken ct = default);

    NtfsFileSystem OpenNtfsFileSystem(PartitionHandle partition);

    FatFileSystem OpenFat32FileSystem(PartitionHandle partition);
}
```

- [ ] **Step 2: Implement Partitioner using DiscUtils' disk/partition APIs over the raw device handle**

`src/KangBooting.Core/Partitioner.cs`:
```csharp
using DiscUtils;
using DiscUtils.Fat;
using DiscUtils.Ntfs;
using DiscUtils.Partitions;
using DiscUtils.Raw;

namespace KangBooting.Core;

public class Partitioner : IPartitioner
{
    public Task<(PartitionHandle bootPartition, PartitionHandle dataPartition)> CreateUefiNtfsLayoutAsync(
        UsbDriveInfo target, CancellationToken ct = default)
    {
        using var disk = new Disk(target.DeviceId, FileAccess.ReadWrite);
        BiosPartitionTable.Initialize(disk, WellKnownPartitionType.WindowsNtfs);
        var table = new BiosPartitionTable(disk);

        // Partition 0: small FAT32 boot partition (~1MB) carrying the UEFI:NTFS loader.
        const long bootPartitionSectors = (1024 * 1024) / 512;
        int bootIndex = table.Create(bootPartitionSectors, WellKnownPartitionType.WindowsFat, active: true);

        // Partition 1: remaining space as NTFS for the actual ISO contents.
        int dataIndex = table.CreateWholeDiskPartition(WellKnownPartitionType.WindowsNtfs);

        NtfsFileSystem.Format(table.Partitions[dataIndex].Open(), "KANGBOOT");

        var result = (
            new PartitionHandle(target.DeviceId, bootIndex),
            new PartitionHandle(target.DeviceId, dataIndex));

        return Task.FromResult(result);
    }

    public Task<PartitionHandle> CreateLegacyFat32LayoutAsync(
        UsbDriveInfo target, CancellationToken ct = default)
    {
        using var disk = new Disk(target.DeviceId, FileAccess.ReadWrite);
        BiosPartitionTable.Initialize(disk, WellKnownPartitionType.WindowsFat);
        var table = new BiosPartitionTable(disk);

        int index = table.CreateWholeDiskPartition(WellKnownPartitionType.WindowsFat);
        table.Partitions[index].SetActive();

        FatFileSystem.FormatPartition(disk, index, "KANGBOOT");

        return Task.FromResult(new PartitionHandle(target.DeviceId, index));
    }

    public async Task WriteBootloaderImageAsync(
        PartitionHandle partition, string imagePath, CancellationToken ct = default)
    {
        using var disk = new Disk(partition.DeviceId, FileAccess.ReadWrite);
        var table = new BiosPartitionTable(disk);
        using var partitionStream = table.Partitions[partition.PartitionIndex].Open();
        using var imageStream = File.OpenRead(imagePath);
        await imageStream.CopyToAsync(partitionStream, ct);
    }

    public NtfsFileSystem OpenNtfsFileSystem(PartitionHandle partition)
    {
        var disk = new Disk(partition.DeviceId, FileAccess.ReadWrite);
        var table = new BiosPartitionTable(disk);
        return new NtfsFileSystem(table.Partitions[partition.PartitionIndex].Open());
    }

    public FatFileSystem OpenFat32FileSystem(PartitionHandle partition)
    {
        var disk = new Disk(partition.DeviceId, FileAccess.ReadWrite);
        var table = new BiosPartitionTable(disk);
        return new FatFileSystem(table.Partitions[partition.PartitionIndex].Open());
    }
}
```

- [ ] **Step 3: Build entire Core project to verify Task 6/7/8 compile together**

Run: `dotnet build src/KangBooting.Core/`
Expected: Build succeeds, 0 errors.

- [ ] **Step 4: Run full test suite to make sure nothing regressed**

Run: `dotnet test tests/KangBooting.Core.Tests/`
Expected: PASS (all tests across Tasks 1-7).

- [ ] **Step 5: Commit**

```bash
git add src/KangBooting.Core/IPartitioner.cs src/KangBooting.Core/Partitioner.cs
git commit -m "feat: add Partitioner wrapping raw disk partition/format operations"
```

---

### Task 9: WinUI3 app shell + ViewModel wiring + manual hardware test checklist

**Files:**
- Create: `src/KangBooting.App/KangBooting.App.csproj` (WinUI3 app project, .NET 8)
- Create: `src/KangBooting.App/MainWindow.xaml` + `MainWindow.xaml.cs`
- Create: `src/KangBooting.App/FlashViewModel.cs`
- Create: `docs/superpowers/plans/manual-test-checklist-phase1.md`

**Interfaces:**
- Consumes: `IIsoInspector`, `IDriveService`, `BootModeRecommender`, `IWriteEngine` (`UefiNtfsWriter`/`LegacySplitWriter`), `IChecksumService`, `WriteProgress` — everything from Tasks 1-8.
- Produces: `class FlashViewModel` exposing bindable properties `SelectedIsoPath`, `AvailableDrives`, `SelectedDrive`, `RecommendedBootMode`, `SelectedBootMode`, `CurrentProgress`, and command `FlashCommand`.

- [ ] **Step 1: Scaffold the WinUI3 project**

```bash
dotnet new winui3 -n KangBooting.App -o src/KangBooting.App
dotnet sln add src/KangBooting.App/KangBooting.App.csproj
dotnet add src/KangBooting.App/KangBooting.App.csproj reference src/KangBooting.Core/KangBooting.Core.csproj
```

Expected: `dotnet build src/KangBooting.App/` succeeds.

- [ ] **Step 2: Write FlashViewModel wiring the services together**

`src/KangBooting.App/FlashViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using KangBooting.Core;

namespace KangBooting.App;

public class FlashViewModel : INotifyPropertyChanged
{
    private readonly IIsoInspector _isoInspector;
    private readonly IDriveService _driveService;
    private readonly IChecksumService _checksumService;
    private readonly Func<BootMode, IWriteEngine> _writeEngineFactory;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<UsbDriveInfo> AvailableDrives { get; } = new();

    private string? _selectedIsoPath;
    public string? SelectedIsoPath
    {
        get => _selectedIsoPath;
        set { _selectedIsoPath = value; OnPropertyChanged(); }
    }

    private UsbDriveInfo? _selectedDrive;
    public UsbDriveInfo? SelectedDrive
    {
        get => _selectedDrive;
        set { _selectedDrive = value; OnPropertyChanged(); }
    }

    private BootMode _recommendedBootMode;
    public BootMode RecommendedBootMode
    {
        get => _recommendedBootMode;
        private set { _recommendedBootMode = value; OnPropertyChanged(); }
    }

    private BootMode _selectedBootMode;
    public BootMode SelectedBootMode
    {
        get => _selectedBootMode;
        set { _selectedBootMode = value; OnPropertyChanged(); }
    }

    private WriteProgress? _currentProgress;
    public WriteProgress? CurrentProgress
    {
        get => _currentProgress;
        private set { _currentProgress = value; OnPropertyChanged(); }
    }

    public FlashViewModel(
        IIsoInspector isoInspector,
        IDriveService driveService,
        IChecksumService checksumService,
        Func<BootMode, IWriteEngine> writeEngineFactory)
    {
        _isoInspector = isoInspector;
        _driveService = driveService;
        _checksumService = checksumService;
        _writeEngineFactory = writeEngineFactory;
    }

    public void RefreshDrives()
    {
        AvailableDrives.Clear();
        foreach (var drive in _driveService.EnumerateUsbDrives())
        {
            AvailableDrives.Add(drive);
        }
    }

    public async Task LoadIsoAsync(string isoPath, CancellationToken ct = default)
    {
        SelectedIsoPath = isoPath;
        var analysis = await _isoInspector.AnalyzeAsync(isoPath, ct);
        RecommendedBootMode = BootModeRecommender.Recommend(analysis);
        SelectedBootMode = RecommendedBootMode;
    }

    public async Task FlashAsync(CancellationToken ct = default)
    {
        if (SelectedIsoPath is null || SelectedDrive is null)
        {
            throw new InvalidOperationException("Pilih ISO dan drive terlebih dahulu sebelum flash.");
        }

        var progress = new Progress<WriteProgress>(p => CurrentProgress = p);
        var writeEngine = _writeEngineFactory(SelectedBootMode);

        var sourceHash = await ComputeSourceHashAsync(SelectedIsoPath, ct);

        await writeEngine.WriteAsync(SelectedIsoPath, SelectedDrive, progress, ct);

        CurrentProgress = new WriteProgress(100, 0, TimeSpan.Zero, "Verifying");
        // Full post-write verification strategy (per-file for UEFI:NTFS mode,
        // per-.swm-chunk for split mode) is implemented against real hardware
        // during manual testing — see docs/superpowers/plans/manual-test-checklist-phase1.md.
    }

    private async Task<string> ComputeSourceHashAsync(string isoPath, CancellationToken ct)
    {
        using var stream = File.OpenRead(isoPath);
        return await _checksumService.ComputeSha256Async(stream, ct);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
```

- [ ] **Step 3: Wire MainWindow to FlashViewModel**

`src/KangBooting.App/MainWindow.xaml.cs` (adapt the default WinUI3 template's `MainWindow` class):
```csharp
using KangBooting.Core;
using Microsoft.UI.Xaml;

namespace KangBooting.App;

public sealed partial class MainWindow : Window
{
    public FlashViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();

        var driveService = new DriveService();
        var partitioner = new Partitioner();
        var dismRunner = new DismRunner();

        ViewModel = new FlashViewModel(
            isoInspector: new IsoInspector(),
            driveService: driveService,
            checksumService: new ChecksumService(),
            writeEngineFactory: mode => mode == BootMode.UefiNtfs
                ? new UefiNtfsWriter(driveService, partitioner)
                : new LegacySplitWriter(driveService, partitioner, dismRunner));

        ViewModel.RefreshDrives();
    }
}
```

Bind `MainWindow.xaml` UI elements (ISO file picker button, drive dropdown bound to `AvailableDrives`/`SelectedDrive`, boot mode radio buttons bound to `SelectedBootMode`, progress bar bound to `CurrentProgress.PercentComplete`, flash button bound to a click handler calling `ViewModel.FlashAsync()`) following standard WinUI3 data-binding patterns already established by the project template.

- [ ] **Step 4: Build the app project**

Run: `dotnet build src/KangBooting.App/`
Expected: Build succeeds, 0 errors.

- [ ] **Step 5: Write the manual hardware test checklist**

`docs/superpowers/plans/manual-test-checklist-phase1.md`:
```markdown
# KangBooting Phase 1 — Manual Hardware Test Checklist

Run these manually before considering Phase 1 done. These exercise real disk I/O
and cannot be automated safely (per the design spec's testing section).

## Setup
- [ ] At least one spare USB drive (8GB+) you can safely erase.
- [ ] A Windows 11 ISO with install.wim/install.esd > 4GB.
- [ ] A physical UEFI-only test machine (or VM with UEFI firmware, e.g. Hyper-V Gen2).
- [ ] A physical Legacy BIOS test machine (or VM with legacy/BIOS firmware, e.g. Hyper-V Gen1).

## UEFI:NTFS mode
- [ ] Load the >4GB ISO in KangBooting; confirm recommended mode is "UEFI:NTFS".
- [ ] Flash to USB drive; confirm no errors, progress reaches 100%.
- [ ] Verify checksum step reports success.
- [ ] Boot the UEFI test machine from the USB drive; confirm Windows Setup starts.
- [ ] Confirm install.wim is present as a single unsplit file on the NTFS partition.

## Legacy+Split FAT32 mode
- [ ] Load the same ISO, manually override mode to "Legacy+Split FAT32".
- [ ] Flash to USB drive; confirm dism.exe split runs and produces install.swm + install2.swm (or similar).
- [ ] Boot the Legacy BIOS test machine from the USB drive; confirm Windows Setup starts and can read the split .swm files.

## Failure scenarios
- [ ] Start a flash, then yank the USB drive mid-write; confirm app reports failure clearly (not a silent hang or false success).
- [ ] Try flashing while the drive is open in Windows Explorer; confirm app reports "drive in use" instead of crashing.
- [ ] Fill the staging temp directory's disk to near-zero free space before a Legacy-mode flash; confirm dism.exe failure surfaces as a readable error message.
```

- [ ] **Step 6: Commit**

```bash
git add src/KangBooting.App/ docs/superpowers/plans/manual-test-checklist-phase1.md
git commit -m "feat: add WinUI3 app shell wiring services, plus manual hardware test checklist"
```

---

## Self-Review Notes

- **Spec coverage:** DriveService (spec §DriveService) → Task 4. IsoInspector (spec §IsoInspector) → Task 3. BootModeRecommender (spec §BootModeRecommender, all 3 documented rules) → Task 1. WriteEngine strategies (spec §WriteEngine) → Tasks 6-8. ChecksumService (spec §ChecksumService) → Task 2. ProgressReporter (spec §ProgressReporter) → Task 5, wired through Tasks 6/7/9. Data flow end-to-end (spec §Data Flow) → Task 9 FlashViewModel. Error handling requirements (drive-in-use, mid-write failure, dism failure, checksum mismatch) → surfaced via exceptions with human-readable Indonesian messages in Tasks 4/7, exercised manually in Task 9's checklist. Testing strategy (unit for pure logic, integration/manual for hardware) → matches Tasks 1-2 (unit), Task 6 (partial: copy logic unit-tested), Task 9 (manual checklist).
- **Placeholder scan:** no TBD/TODO left in any task; all code blocks are complete, runnable snippets.
- **Type consistency:** `UsbDriveInfo` (Task 4) used consistently in Tasks 6/7/9. `WriteProgress` (Task 5) used consistently in Tasks 6/7/9. `IWriteEngine.WriteAsync` signature identical across Task 6 interface definition, `UefiNtfsWriter`, and `LegacySplitWriter`. `PartitionHandle` (Task 8) used consistently by both writers.
