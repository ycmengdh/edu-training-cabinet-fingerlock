# Debug tools

This directory contains hardware diagnostics and protocol test utilities. Run
commands from this directory unless a script documents otherwise.

## Requirements

- Python 3.10+
- `pyserial`
- PowerShell 7+ for `mesh_serial_diagnostics.ps1`
- Root and cabinet serial ports must not be open in the desktop application

Install the Python dependency with:

```powershell
python -m pip install pyserial
```

## Common commands

Probe the Root-to-cabinet Mesh link:

```powershell
python communication_stress.py --links mesh --root-port COM16 `
  --cabinet-id CAB_ACA704E38AA0 --count 20
```

Probe a cabinet through its UART0 debug port:

```powershell
python communication_stress.py --links uart --cabinet-port COM19 `
  --cabinet-id CAB_ACA704E38AA0 --count 20
```

Set the Root and cabinet to the same Mesh channel:

```powershell
python mesh_channel_config.py --channel 1 --root-port COM16 `
  --cabinet-port COM19 --root-id ROOT_B81F3FA9F404 `
  --cabinet-id CAB_ACA704E38AA0
```

Run SD chunk upload verification:

```powershell
python verify_sd_chunk_upload.py --help
```

Most legacy scripts have hardware-specific default COM ports. Check their
constants or command-line help before running them. Binary captures produced by
the scripts belong in `debug/output/`, which is intentionally ignored by Git.
