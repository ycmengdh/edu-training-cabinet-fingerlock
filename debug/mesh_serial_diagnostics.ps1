param(
    [string]$RootPort = "COM16",
    [string]$CabinetPort = "COM12",
    [int]$DurationSeconds = 35,
    [switch]$SendCabinetStatus,
    [switch]$RebootCabinet
)

$ErrorActionPreference = "Stop"

function Add-Le16 {
    param(
        [System.Collections.Generic.List[byte]]$List,
        [int]$Value
    )
    $List.Add([byte]($Value -band 0xFF))
    $List.Add([byte](($Value -shr 8) -band 0xFF))
}

function Get-Crc16 {
    param([byte[]]$Data)
    $crc = 0xFFFF
    foreach ($item in $Data) {
        $crc = $crc -bxor $item
        for ($bit = 0; $bit -lt 8; $bit++) {
            if (($crc -band 1) -ne 0) {
                $crc = (($crc -shr 1) -bxor 0xA001) -band 0xFFFF
            } else {
                $crc = ($crc -shr 1) -band 0xFFFF
            }
        }
    }
    return $crc
}

function New-AppFrame {
    param(
        [int]$Command,
        [int]$MessageId,
        [string]$DeviceId,
        [string]$Json
    )

    $deviceBytes = [Text.Encoding]::UTF8.GetBytes($DeviceId)
    $payloadBytes = [Text.Encoding]::UTF8.GetBytes($Json)
    $app = [System.Collections.Generic.List[byte]]::new()
    $app.AddRange([byte[]](0xB1, 0x0F, 0x01, 0x00))
    Add-Le16 $app $Command
    Add-Le16 $app $MessageId
    Add-Le16 $app 0
    $app.Add([byte]$deviceBytes.Length)
    $app.Add(0)
    Add-Le16 $app $payloadBytes.Length
    $app.AddRange([byte[]](0, 0, 0, 0))
    $app.AddRange($deviceBytes)
    $app.AddRange($payloadBytes)

    $body = [System.Collections.Generic.List[byte]]::new()
    $body.Add(0x01)
    $body.Add([byte](($app.Count -shr 8) -band 0xFF))
    $body.Add([byte]($app.Count -band 0xFF))
    $body.AddRange($app.ToArray())
    $crc = Get-Crc16 $body.ToArray()

    $frame = [System.Collections.Generic.List[byte]]::new()
    $frame.AddRange([byte[]](0xA5, 0x5A))
    $frame.AddRange($body.ToArray())
    $frame.Add([byte](($crc -shr 8) -band 0xFF))
    $frame.Add([byte]($crc -band 0xFF))
    return $frame.ToArray()
}

function Read-Available {
    param(
        [IO.Ports.SerialPort]$Port,
        [System.Collections.Generic.List[byte]]$Sink
    )
    $available = $Port.BytesToRead
    if ($available -le 0) {
        return
    }
    $buffer = New-Object byte[] $available
    $read = $Port.Read($buffer, 0, $available)
    for ($index = 0; $index -lt $read; $index++) {
        $Sink.Add($buffer[$index])
    }
}

function Decode-Capture {
    param(
        [string]$Tag,
        [byte[]]$Bytes
    )

    $offset = 0
    $frames = 0
    $crcErrors = 0
    $counts = @{}
    $lines = [System.Collections.Generic.List[string]]::new()

    while ($offset -le $Bytes.Length - 7) {
        if ($Bytes[$offset] -ne 0xA5 -or $Bytes[$offset + 1] -ne 0x5A) {
            $offset++
            continue
        }

        $version = [int]$Bytes[$offset + 2]
        $length = (([int]$Bytes[$offset + 3]) -shl 8) -bor [int]$Bytes[$offset + 4]
        $end = $offset + 7 + $length
        if ($length -le 0 -or $end -gt $Bytes.Length) {
            $offset++
            continue
        }

        $body = New-Object byte[] (3 + $length)
        [Array]::Copy($Bytes, $offset + 2, $body, 0, $body.Length)
        $calculated = Get-Crc16 $body
        $received = (([int]$Bytes[$offset + 5 + $length]) -shl 8) -bor
                    [int]$Bytes[$offset + 6 + $length]
        if ($calculated -ne $received) {
            $crcErrors++
            $offset++
            continue
        }

        $frames++
        $payload = New-Object byte[] $length
        [Array]::Copy($Bytes, $offset + 5, $payload, 0, $length)
        if ($version -eq 1 -and $length -ge 18 -and
            $payload[0] -eq 0xB1 -and $payload[1] -eq 0x0F) {
            $command = [int]$payload[4] -bor (([int]$payload[5]) -shl 8)
            $commandKey = "0x{0:X4}" -f $command
            if ($counts.ContainsKey($commandKey)) {
                $counts[$commandKey]++
            } else {
                $counts[$commandKey] = 1
            }

            $messageId = [int]$payload[6] -bor (([int]$payload[7]) -shl 8)
            $deviceLength = [int]$payload[10]
            $sourceLength = [int]$payload[11]
            $dataLength = [int]$payload[12] -bor (([int]$payload[13]) -shl 8)
            $dataOffset = 18 + $deviceLength + $sourceLength
            $deviceId = if ($deviceLength -gt 0) {
                [Text.Encoding]::UTF8.GetString($payload, 18, $deviceLength)
            } else {
                ""
            }
            $data = if ($dataLength -gt 0 -and
                        $dataOffset + $dataLength -le $payload.Length) {
                [Text.Encoding]::UTF8.GetString($payload, $dataOffset, $dataLength)
            } else {
                ""
            }

            if ($command -eq 0x0006) {
                try {
                    $message = [string](($data | ConvertFrom-Json).msg)
                    if ($message -match "MESH|MAIN|STORAGE|MSG|FRAME|fail|error|panic|restart|permission") {
                        $lines.Add("[$Tag] $message")
                    }
                } catch {
                    $lines.Add("[$Tag] malformed LOG payload: $data")
                }
            } else {
                $lines.Add(("[$Tag] APP 0x{0:X4} mid={1} did={2} data={3}" -f
                            $command, $messageId, $deviceId, $data))
            }
        }
        $offset = $end
    }

    $ascii = [Text.Encoding]::ASCII.GetString($Bytes)
    if ($ascii.Contains("PONG")) {
        Write-Output "[$Tag] plaintext PONG received"
    }
    $lines | Select-Object -First 240
    $countText = ($counts.GetEnumerator() | Sort-Object Name | ForEach-Object {
        $_.Name + "=" + $_.Value
    }) -join " "
    Write-Output "[$Tag] COUNTS $countText"
    Write-Output "[$Tag] SUMMARY bytes=$($Bytes.Length) frames=$frames crc_bad=$crcErrors"
}

$root = [IO.Ports.SerialPort]::new($RootPort, 921600, "None", 8, "One")
$cabinet = [IO.Ports.SerialPort]::new($CabinetPort, 921600, "None", 8, "One")
foreach ($port in @($root, $cabinet)) {
    $port.ReadBufferSize = 1MB
    $port.ReadTimeout = 20
    $port.DtrEnable = $false
    $port.RtsEnable = $false
}

$rootBytes = [System.Collections.Generic.List[byte]]::new()
$cabinetBytes = [System.Collections.Generic.List[byte]]::new()
try {
    $root.Open()
    $cabinet.Open()
    $root.DiscardInBuffer()
    $cabinet.DiscardInBuffer()
    $started = [DateTime]::UtcNow
    $sent = $false
    while (([DateTime]::UtcNow - $started).TotalSeconds -lt $DurationSeconds) {
        Read-Available $root $rootBytes
        Read-Available $cabinet $cabinetBytes
        if (-not $sent -and ([DateTime]::UtcNow - $started).TotalSeconds -ge 2) {
            if ($RebootCabinet) {
                $frame = New-AppFrame 0x0038 62001 "CAB_ACA704E38558" '{"mode":"mesh"}'
                $cabinet.Write($frame, 0, $frame.Length)
            } elseif ($SendCabinetStatus) {
                $frame = New-AppFrame 0x0034 62000 "CAB_ACA704E38558" "{}"
                $cabinet.Write($frame, 0, $frame.Length)
            }
            $sent = $true
        }
        Start-Sleep -Milliseconds 5
    }
    Read-Available $root $rootBytes
    Read-Available $cabinet $cabinetBytes
} finally {
    if ($root.IsOpen) { $root.Close() }
    if ($cabinet.IsOpen) { $cabinet.Close() }
}

Decode-Capture "ROOT" $rootBytes.ToArray()
Decode-Capture "CAB" $cabinetBytes.ToArray()
