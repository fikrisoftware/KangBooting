# KangBooting — Fase 1: Single-ISO Bootable USB Flasher

## Latar Belakang

Rufus dan Ventoy sering bermasalah saat membuat USB bootable dari ISO besar (>4GB), khususnya karena batas ukuran file 4GB di filesystem FAT32 — yang jadi masalah untuk ISO Windows modern dengan `install.wim`/`install.esd` besar. KangBooting fase 1 fokus menyelesaikan kasus ini secara robust untuk single ISO, dengan dukungan dua mode boot (UEFI-only via NTFS, dan Legacy+UEFI via split FAT32), plus rekomendasi otomatis mode mana yang cocok.

Fitur multi-ISO persistence (ala Ventoy) sengaja **tidak** masuk fase ini — akan jadi spec terpisah (fase 2) karena kompleksitasnya (partisi khusus + boot menu loader).

## Target Platform & Stack

- Windows only (fase 1). Arsitektur dipisah lewat interface/service layer supaya porting cross-platform (fase depan) lebih mudah.
- C# / .NET, UI WinUI3, pola MVVM.
- Library: DiscUtils (baca/tulis image ISO/NTFS/FAT tanpa mount), P/Invoke Win32 (`DeviceIoControl`, `SetupAPI`) untuk akses disk raw, `dism.exe` (shell-out resmi Microsoft) untuk split `install.wim`.
- Bootloader UEFI:NTFS dibundel dari proyek akeo/rufus (MIT license) — tidak ditulis ulang dari nol.

## Arsitektur

```
UI (WinUI3, MVVM)
  └─ ViewModels
       └─ Services
            ├─ DriveService        (enumerasi & lock USB drive)
            ├─ IsoInspector        (analisa ISO)
            ├─ BootModeRecommender (logic rekomendasi mode boot)
            ├─ WriteEngine         (strategy: UefiNtfsWriter | LegacySplitWriter)
            ├─ ChecksumService     (SHA256 verify)
            └─ ProgressReporter    (IProgress<WriteProgress>)
```

## Komponen

### DriveService
Enumerasi USB drive via WMI `Win32_DiskDrive` (filter `InterfaceType='USB'`). Lock exclusive pakai `DeviceIoControl` dengan `FSCTL_LOCK_VOLUME` sebelum operasi tulis, supaya tidak bentrok dengan proses lain (Explorer, antivirus scan, dsb).

### IsoInspector
Buka ISO pakai DiscUtils (baca ISO 9660/UDF tanpa mount ke drive letter). Ekstrak informasi:
- Ukuran file `install.wim` / `install.esd` (jika ada)
- Keberadaan boot sector BIOS (`[BOOT]/etfsboot.com` atau `boot.bin` di root)
- Keberadaan `efi/boot/bootx64.efi` (dukungan UEFI)

### BootModeRecommender
Pure logic (tidak menyentuh hardware), gampang di-unit-test. Aturan:
- Jika `install.wim`/`.esd` > 4GB **dan** ISO tidak berisi boot sector BIOS → rekomendasi **UEFI:NTFS** (karena tidak perlu split, dan tidak ada kebutuhan Legacy BIOS).
- Jika ISO berisi boot sector BIOS (butuh dukungan Legacy) **dan** ada file >4GB → rekomendasi **Legacy+Split FAT32**.
- Jika tidak ada file >4GB sama sekali → kedua mode valid, default ke UEFI:NTFS (lebih simpel & modern).
- User selalu bisa override rekomendasi secara manual di UI.

### WriteEngine (Strategy Pattern)

**UefiNtfsWriter**
1. Format target drive: partisi utama NTFS (isi seluruh ISO apa adanya, tanpa split — NTFS tidak punya batas 4GB per file).
2. Buat partisi kecil (~1MB, FAT32) berisi bootloader UEFI:NTFS (bundled binary).
3. Set partisi kecil sebagai partisi aktif/EFI system partition sesuai skema GPT/MBR target.
4. Copy seluruh isi ISO ke partisi NTFS via DiscUtils.

**LegacySplitWriter**
1. Format target drive: FAT32 (single partition, bootable flag utk BIOS).
2. Extract seluruh isi ISO ke FAT32 via DiscUtils.
3. Untuk `install.wim`/`.esd` >4GB: jalankan `dism.exe /Split-Image /ImageFile:<wim> /SWMFile:<output>.swm /FileSize:4000` untuk memecah jadi beberapa file `.swm` <4GB, lalu hapus file asli.
4. Salin boot sector BIOS + struktur boot yang diperlukan.

### ChecksumService
Hitung SHA256 dari source ISO. Setelah proses tulis selesai, hitung ulang checksum dari isi hasil tulis (untuk mode UEFI:NTFS: hash file-per-file dibanding source; untuk mode split: verifikasi masing-masing potongan `.swm` sesuai metadata DISM) dan bandingkan.

### ProgressReporter
`IProgress<WriteProgress>` dengan field: `PercentComplete`, `BytesPerSecond`, `EstimatedTimeRemaining`, `CurrentOperation` (string, misal "Formatting", "Copying files", "Splitting install.wim", "Verifying"). Di-stream ke UI secara real-time selama operasi async berjalan.

## Data Flow

1. User pilih file ISO.
2. `IsoInspector` menganalisa ISO (async, tampilkan loading state).
3. `BootModeRecommender` menghasilkan rekomendasi mode boot; UI menampilkan rekomendasi + opsi override manual.
4. User pilih USB drive target dari daftar `DriveService`.
5. Konfirmasi eksplisit dari user (peringatan: semua data di drive akan hilang).
6. `WriteEngine` (strategi sesuai mode terpilih) jalan async, `ProgressReporter` update UI real-time.
7. Setelah tulis selesai, `ChecksumService` verifikasi otomatis.
8. Tampilkan hasil akhir: sukses (dengan ringkasan) atau gagal (dengan pesan error spesifik).

## Error Handling

- **Drive sedang dipakai proses lain**: gagal lock volume → tampilkan pesan jelas ke user ("Drive sedang digunakan aplikasi lain, tutup dulu"), jangan retry diam-diam tanpa info.
- **USB tercabut / bad sector saat menulis**: deteksi exception I/O di tengah proses → tandai proses gagal, tampilkan status "USB kemungkinan corrupt, coba drive lain", jangan melaporkan sukses palsu.
- **`dism.exe` split gagal** (misal disk space kurang, permission): parse exit code & stderr dism, tampilkan pesan spesifik ke user — bukan stack trace mentah.
- **Checksum mismatch pasca-tulis**: tampilkan sebagai kegagalan verifikasi, sarankan user coba ulang atau ganti USB drive.
- Semua exception di boundary Service→UI ditangkap dan diterjemahkan ke pesan human-readable; log detail teknis disimpan terpisah untuk debugging, tidak ditampilkan mentah ke user.

## Testing

- **Unit test**: `BootModeRecommender` (pure logic, banyak skenario kombinasi ukuran file & boot sector), `ChecksumService` (hash comparison logic).
- **Integration test**: `WriteEngine` diuji pakai VHD/virtual disk kalau API Windows memungkinkan testing tanpa hardware fisik.
- **Manual test**: validasi akhir wajib pakai USB fisik & ISO nyata (termasuk ISO Windows 11 dengan install.wim >4GB) untuk kedua mode boot, di device UEFI dan Legacy BIOS asli.

## Di Luar Scope Fase 1

- Multi-ISO persistence / boot menu (ala Ventoy) — fase 2, spec terpisah.
- Cross-platform (Linux/Mac) — fase depan, arsitektur sudah disiapkan lewat service layer tapi implementasi native Windows dulu.
- Custom partition scheme lanjutan, plugin system, atau UI theming lanjutan.
