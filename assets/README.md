# assets/uefi-ntfs.img

Source: akeo.freeware "uefi-ntfs" project (https://github.com/pbatard/uefi-ntfs), MIT License.
Used unmodified as the small FAT32 boot partition payload for UEFI:NTFS mode,
so a UEFI firmware that cannot read NTFS natively can chain-load into the NTFS
partition containing the actual Windows installer files.

PLACEHOLDER: this file must be replaced with the real binary (downloaded manually by a human from the official release) before this code can produce a real bootable USB drive. Automated tooling did not fetch it.
