# KangBooting Phase 2 — Multi-OS (Multi-Boot) Plan

## Tujuan

Satu USB drive bisa boot ke beberapa ISO Windows berbeda (misal Win10 + Win11 + WinPE), pilih lewat menu boot saat startup. Bukan Ventoy-clone (ISO passthrough tanpa extract) — itu proyek terpisah yang jauh lebih besar (bertahun-tahun kerja Ventoy nangani ratusan distro). Ini versi yang buildable dengan basis kode yang sudah ada: **tiap ISO dapet partisi NTFS sendiri, satu boot manager bersama (rEFInd) buat milih partisi mana yang di-boot.**

## Batasan yang disepakati

- **UEFI only.** rEFInd (boot manager yang dipakai) tidak mendukung BIOS/Legacy. Mode Legacy+Split FAT32 yang sudah ada tetap single-ISO seperti sekarang — multi-OS cuma opsi ketiga baru, bukan pengganti.
- **Bukan ISO passthrough.** Tiap ISO di-extract/copy penuh ke partisi NTFS sendiri (pakai mekanisme mount+copy yang sudah ada dari Phase 1). Artinya butuh ruang USB = total semua ISO + overhead, bukan cuma ukuran ISO terbesar kayak Ventoy.
- **Nambah ISO baru = format ulang total.** Karena partisi jumlahnya fix saat pertama kali dibuat (GPT, bukan filesystem tunggal), tidak ada cara "tambah satu ISO lagi" tanpa wipe & rebuild seluruh layout partisi.
- **rEFInd binary tidak didownload otomatis** — sama seperti `bootx64_signed.efi` sebelumnya, ini biner pihak ketiga (BSD license, dari sourceforge.net/projects/refind atau GitHub rEFInd releases), user download manual.

## Arsitektur

### Layout partisi (GPT, bukan MBR — MBR maks 4 partisi primer, kurang buat N ISO + 1 boot)

```
Partisi 0: ESP (FAT32, ~200MB)       — rEFInd + driver NTFS + refind.conf
Partisi 1: NTFS (ukuran ISO#1 + margin) — isi ISO Windows #1
Partisi 2: NTFS (ukuran ISO#2 + margin) — isi ISO Windows #2
...
Partisi N: NTFS (ukuran ISO#N + margin) — isi ISO Windows #N
```

### Kenapa rEFInd, bukan bikin boot menu sendiri

rEFInd (open-source, BSD license) sudah punya driver filesystem NTFS bawaan (`ntfs_x64.efi`) dan bisa **auto-detect** instalasi Windows di partisi lain pada disk yang sama tanpa konfigurasi manual per-OS — dia baca `/efi/boot/bootx64.efi`/`bootmgfw.efi` di tiap partisi dan otomatis munculin sebagai entry menu. Ini persis kebutuhan kita: satu boot manager, banyak partisi Windows, tanpa perlu nulis ulang logic auto-detect dari nol.

### Alur penulisan (mirip Phase 1, di-loop per ISO)

1. Format disk sebagai GPT.
2. Buat partisi ESP kecil, format FAT32, salin rEFInd + driver NTFS + `refind.conf` yang di-generate (label tiap entry pakai nama ISO, biar gak cuma "Windows Boot Manager" generik semua).
3. Untuk tiap ISO yang dipilih user (loop):
   - Buat partisi NTFS baru (ukuran = size ISO + margin).
   - Format NTFS, kasih volume label = nama ISO (biar gampang dikenali & mempermudah rEFInd labeling).
   - Mount ISO (reuse `IsoMounter` dari Phase 1) → copy isi ke partisi NTFS itu (reuse `RealFileSystemCopier`) — **tanpa bikin partisi boot kecil terpisah kayak Phase 1's UefiNtfsWriter**, karena boot-nya udah ditangani rEFInd yang baca partisi ini via driver NTFS-nya.
4. Refresh partition table, selesai.

### Komponen baru

- `Partitioner.CreateMultiBootLayoutAsync(target, isoSizes[])` — bikin GPT + ESP + N partisi NTFS sekaligus (return `List<PartitionHandle>`).
- `RefindInstaller` (baru, mirip pola `BootsectRunner`/`DismRunner`) — salin rEFInd binary + driver + generate `refind.conf` ke ESP. **Bukan shell-out** (rEFInd gak perlu dijalankan, cuma disalin filenya) — jadi ini lebih ke file-copy helper, bukan process runner.
- `MultiBootWriter : IWriteEngine` — orkestrasi keseluruhan, tapi `WriteAsync`-nya butuh **banyak ISO path**, bukan satu — perlu ubah `IWriteEngine` interface atau bikin interface terpisah `IMultiBootWriter` dengan signature `WriteAsync(IReadOnlyList<string> isoPaths, UsbDriveInfo target, ...)`. **Keputusan desain:** bikin interface terpisah (`IMultiBootWriter`), jangan paksa `IWriteEngine` yang ada nampung ini — beda bentuk data (satu ISO vs banyak), maksa ke bentuk lama bakal jelek.
- `BootMode` enum nambah value `MultiOs` (atau bikin flow terpisah di UI sejak awal, karena `BootModeRecommender` yang ada juga gak relevan buat kasus banyak ISO).

### UI

- Mode "Multi-OS" jadi pilihan ketiga (radio button/tab terpisah dari UEFI:NTFS/Legacy).
- Ganti single ISO picker jadi list: tombol "Tambah ISO", tiap entry di list bisa dihapus, tampilkan ukuran tiap ISO + total.
- Validasi ukuran drive: total semua ISO + ESP overhead harus muat.
- Checklist proses (`ProcessStepViewModel`) perlu extend jadi per-ISO (misal "Menyalin ISO 2 dari 3") bukan cuma 4 step tetap.

## Yang perlu divalidasi empiris dulu sebelum full-commit ngoding (pola yang sama kayak Phase 1 — jangan asumsi doang)

1. **rEFInd + NTFS driver beneran bisa chainload Windows Boot Manager dari partisi NTFS tanpa ESP terpisah per-OS.** Ini asumsi inti seluruh desain. Perlu tes: format 1 ESP + rEFInd + driver NTFS, taruh 1 Windows install NTFS partition manual (tanpa boot partition kecil), coba boot — pastikan rEFInd nemuin & bisa chainload.
2. **Auto-detect rEFInd bisa dikustom label-nya** per volume label NTFS (biar user gak bingung liat "Windows Boot Manager" berkali-kali tanpa keterangan) — cek dokumentasi refind.conf `scanfor`/`showlabel` options.
3. **DiscUtils bisa bikin GPT partition table dgn benar** (Phase 1 cuma pernah pakai `BiosPartitionTable`/MBR — GPT pakai `DiscUtils.Partitions.GuidPartitionTable`, API beda, belum pernah dites di project ini).

## Rencana task (kalau lanjut implementasi)

1. Riset+validasi 3 poin di atas (manual, di hardware asli, sebelum nulis kode produksi) — via VM (Hyper-V Gen2/UEFI) biar gak perlu USB fisik tiap iterasi.
2. `Partitioner.CreateMultiBootLayoutAsync` — GPT + ESP + N partisi NTFS. Unit test terbatas (GPT math bisa dites via DiscUtils di memory, sama kayak Task 8 Phase 1).
3. `RefindInstaller` — copy rEFInd+driver+generate config ke ESP.
4. `IMultiBootWriter`/`MultiBootWriter` — orkestrasi loop per-ISO, reuse `IsoMounter`+`RealFileSystemCopier`.
5. UI: multi-ISO picker, mode "Multi-OS", progress checklist per-ISO.
6. Manual hardware test checklist baru: boot ke tiap OS dari menu rEFInd, verifikasi label, verifikasi tiap partisi bener isinya.

## Effort estimate kasar

Ini bukan task kecil — realistis 1.5–2x scope Phase 1 (GPT itu API baru, rEFInd integration perlu riset dokumentasi + testing manual yang gak bisa di-otomasi, UI multi-select perlu desain ulang bagian penting). Rekomendasi: kerjain sebagai increment terpisah dari fitur-fitur single-ISO yang sekarang, jangan digabung sekali jalan.
