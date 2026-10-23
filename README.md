# OpenGZH
## Building the executable

The game executable lives directly in this folder as `OpenGZH.exe` (a single self-contained
file; it finds `mods/`, `content/` and `fonts/` next to it). To rebuild it:

    dotnet publish src/OpenGZH.Game -c Release -r win-x64 -o publish
    copy publish\OpenGZH.exe .

For macOS or Linux use `-r osx-arm64`, `-r osx-x64` or `-r linux-x64` (the file is then called `OpenGZH`).
