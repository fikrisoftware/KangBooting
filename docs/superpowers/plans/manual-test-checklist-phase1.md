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
