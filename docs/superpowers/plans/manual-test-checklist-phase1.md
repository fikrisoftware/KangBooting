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
- [ ] UNVERIFIED ON REAL HARDWARE: `LegacySplitWriter` now resolves the freshly-formatted FAT32 partition's drive letter via WMI (`DriveService.GetDriveLetterForPartition`) and shells out to `bootsect.exe /nt60 <letter>: /mbr /force`. This depends on Windows re-reading the partition table (`IOCTL_DISK_UPDATE_PROPERTIES`, called from `Partitioner`) and assigning a drive letter within a short retry window. If this checklist item fails, check whether the drive letter resolution timed out (see `BootsectRunner`/`LegacySplitWriter` logs/exception message) — that is the most likely failure point.

## Failure scenarios
- [ ] Start a flash, then yank the USB drive mid-write; confirm app reports failure clearly (not a silent hang or false success).
- [ ] Try flashing while the drive is open in Windows Explorer; confirm app reports "drive in use" instead of crashing.
- [ ] Fill the staging temp directory's disk to near-zero free space before a Legacy-mode flash; confirm dism.exe failure surfaces as a readable error message.
- [ ] Verify `DriveService.LockVolume` actually prevents concurrent access to the drive during a flash (e.g. try to open the raw disk or its volume from another process mid-write). `FSCTL_LOCK_VOLUME` is a volume-scoped control code, but it is currently issued against a `\\.\PHYSICALDRIVEn` (physical-disk) handle, not a `\\.\X:` volume handle — this is a suspected semantic mismatch (see comment in `DriveService.LockVolume`). If it silently no-ops, the lock provides no real protection and would need rearchitecting to enumerate and lock the disk's child volumes instead.
- [ ] Try flashing an install.esd > 4GB in Legacy+Split mode; confirm either a clear, fast, pre-flight Indonesian error message ("format ini belum didukung...") or a successful dism.exe split — not a silent mid-copy failure.
- [ ] After `IOCTL_DISK_UPDATE_PROPERTIES` refreshes the partition table, Windows may auto-mount the new volume before writes to it via the raw partition stream complete — combined with `LockVolume`'s known gap, this could cause the OS's cache to race with or overwrite raw writes. Watch for silent data corruption or inconsistent file listings after a flash, especially on the data partition, and verify a clean unmount/eject before considering a write complete.
