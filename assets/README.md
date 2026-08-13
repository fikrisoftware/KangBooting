# assets/bootx64_signed.efi

Source: pbatard/uefi-ntfs release v2.8 (https://github.com/pbatard/uefi-ntfs/releases/tag/v2.8),
asset `bootx64_signed.efi`. MIT License.

This is the raw x64 Secure-Boot-signed UEFI bootloader binary itself — upstream does
NOT distribute a pre-built disk image. At write time, KangBooting formats the small
boot partition as FAT (see `Partitioner.CreateUefiNtfsLayoutAsync`) and copies this
file's contents onto it as `EFI\Boot\bootx64.efi` (see
`Partitioner.WriteBootloaderImageAsync` / `PlaceBootloader`) — the default path UEFI
firmware probes when no other boot entry is configured. It is NOT byte-copied onto
the partition as a raw disk image.
