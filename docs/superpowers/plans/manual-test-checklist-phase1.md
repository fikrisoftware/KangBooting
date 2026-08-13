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
- [ ] Checksum verification is not yet implemented in Phase 1 — manually verify integrity by comparing file sizes on the USB drive against the ISO contents (or spot-check a large file like install.wim) after the write.
- [ ] Boot the UEFI test machine from the USB drive; confirm Windows Setup starts.
- [ ] Confirm install.wim is present as a single unsplit file on the NTFS partition.

## Legacy+Split FAT32 mode
- [ ] Load the same ISO, manually override mode to "Legacy+Split FAT32".
- [ ] Flash to USB drive; confirm dism.exe split runs and produces install.swm + install2.swm (or similar).
- [ ] Boot the Legacy BIOS test machine from the USB drive; confirm Windows Setup starts and can read the split .swm files.
- [ ] `LegacySplitWriter` resolves the FAT32 partition's drive letter directly from `Partitioner.CreateLegacyFat32LayoutAsync`'s return value (native `New-Partition -AssignDriveLetter`/`Format-Volume`, no polling needed) and shells out to `bootsect.exe /nt60 <letter>: /mbr /force`. Confirm this drive letter is valid immediately after formatting with no delay/retry required.

## Architecture note (superseded DiscUtils-based partitioning)

Partitioning and formatting were originally implemented via DiscUtils (raw MBR/partition-table writes, DiscUtils' FAT/NTFS formatters), which produced three separate real-hardware failures: `FatFileSystem.FormatPartition` rejecting a too-small boot partition, `NtfsFileSystem.Format` throwing "Corrupt record" against stale on-disk state from a prior DiscUtils-written layout, and general MBR partition-type-byte uncertainty. `Partitioner` now shells out to PowerShell's native `Clear-Disk`/`Initialize-Disk`/`New-Partition`/`Format-Volume` cmdlets instead (the same cmdlets Windows Setup and diskpart use), which also eliminates `DriveService.LockVolume` and the drive-letter-polling retry loop entirely — the native cmdlets handle volume locking and drive-letter assignment synchronously as part of their own job. DiscUtils remains only for the ISO-reading fallback (`IsoFileSystemOpener`), used when native ISO mounting (`IsoMounter`) fails.

## Failure scenarios
- [ ] Start a flash, then yank the USB drive mid-write; confirm app reports failure clearly (not a silent hang or false success).
- [ ] Try flashing while the drive is open in Windows Explorer; confirm app reports a clear "drive in use" style error instead of crashing (native `Format-Volume`/`New-Partition` should surface this on their own — verify the resulting PowerShell stderr message is what the app surfaces to the user).
- [ ] Fill the staging temp directory's disk to near-zero free space before a Legacy-mode flash; confirm dism.exe failure surfaces as a readable error message.
- [ ] Try flashing an install.esd > 4GB in Legacy+Split mode; confirm either a clear, fast, pre-flight Indonesian error message ("format ini belum didukung...") or a successful dism.exe split — not a silent mid-copy failure.
- [ ] Flash the same USB drive twice in a row (Retry or a fresh Flash) without unplugging it between attempts; confirm `Clear-Disk -RemoveData -RemoveOEM` cleanly wipes any partition/volume state left by the previous attempt and the second attempt succeeds.
