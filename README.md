# NtripRecorder

A small Windows tool from the AgOpenGPS project to test NTRIP casters and record what
they answer. Use it when RTK corrections do not work in AgOpenGPS/AgIO and you want to
find out whether the problem is the caster, the mountpoint, your login, or your settings.

It can:

- fetch and show the **sourcetable** of a caster,
- connect to a **mountpoint** (NTRIP v1 or v2), optionally sending a GGA position,
- classify the answer (OK, wrong login, unknown mountpoint, HTML error page, ...),
- count the **RTCM3 messages** received (1004, 1077, 1230, ...) and show what they are,
- save the full conversation to a `.rec.txt` file **with your login removed**.

## Quick start (just run it)

1. Download `NtripRecorder-1.0.0.zip` from the [Releases](../../releases) page.
2. Unzip it to any folder.
3. Double-click **`Start-UI.bat`**. Your browser opens at `http://localhost:8765/`.
4. Fill in host, port, mountpoint, username and password, then press the run button.

Requirements: Windows 10/11 (uses the built-in .NET Framework 4.8, nothing to install).
Nothing is sent anywhere except to the caster you enter. The web page only listens on `localhost`.

### Command line

```
NtripRecorder.exe --host caster.example.com [--port 2101] [--mount MOUNT]
                  [--user U] [--pass P] [--version 1|2]
                  [--gga lat,lon[,alt]] [--seconds 20] [--out file.rec.txt]
NtripRecorder.exe --ui [--port 8765] [--no-browser]
```

- Without `--mount` the sourcetable is requested.
- Many casters only send data after they receive a position: add `--gga 52.1,5.1,10`
  with a position near your field.
- Recordings from the web page go to a `recordings` folder next to the exe (only when
  "Save recordings to disk" is ticked).

### Sharing a recording

Credentials are removed, but check the file for anything else personal (your position in
the GGA line, caster name) before you attach it to an issue.

## Build from source

Requirements: Windows, [.NET SDK 9](https://dotnet.microsoft.com/download) (see `global.json`;
any 9.0.x works).

```
git clone https://github.com/AgOpenGPS-Official/NtripRecorder.git
cd NtripRecorder
dotnet run --project src/AgLibrary.Ntrip.Recorder -- --ui
```

Release build:

```
dotnet build -c Release
```

The output is in `src/AgLibrary.Ntrip.Recorder/bin/Release/net48/`.

## Layout

- `src/AgLibrary.Ntrip.Recorder` - the tool (command line + local web page)
- `src/AgLibrary.Ntrip` - NTRIP protocol core (header scanning, chunked decoding, RTCM3 framing).
  A snapshot copy of the library used by the AgOpenGPS NTRIP client rewrite.

## License

See [LICENSE](LICENSE).
