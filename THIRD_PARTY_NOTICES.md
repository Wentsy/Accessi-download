# Third-party notices

Accessi-download 的 portable 發佈包會包含下列獨立第三方執行檔。各元件維持其原始授權。

## yt-dlp

- Project: https://github.com/yt-dlp/yt-dlp
- Binary channel used by CI: https://github.com/yt-dlp/yt-dlp-nightly-builds
- The official yt-dlp Windows PyInstaller binary contains GPLv3+ licensed code. See the yt-dlp project for its current license and third-party notices.

## FFmpeg / FFprobe

- Project: https://ffmpeg.org/
- Build source used by CI: https://github.com/yt-dlp/FFmpeg-Builds
- The portable package currently uses the GPL static Windows x64 build.
- FFmpeg licensing depends on enabled build components. The selected build is marked GPL by its distributor.

## Deno

- Project: https://github.com/denoland/deno
- License information: see the Deno repository.

## Notes

These executables are distributed as separate programs and invoked by Accessi-download as child processes.  
When redistributing a portable package, keep this notice and follow the current license requirements of each upstream project.
