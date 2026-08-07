param(
    [string]$Port = "COM16",
    [int]$Baud = 921600,
    [int]$Seconds = 180,
    [switch]$ActiveProbe
)

$ErrorActionPreference = "Stop"
$serial = [System.IO.Ports.SerialPort]::new($Port, $Baud, "None", 8, "One")
$serial.DtrEnable = $false
$serial.RtsEnable = $false
$serial.ReadTimeout = 100
$serial.ReadBufferSize = 65536

$state = 0
$version = 0
$length = 0
$payload = [System.Collections.Generic.List[byte]]::new()
$crcBytes = [System.Collections.Generic.List[byte]]::new()
$plain = [System.Text.StringBuilder]::new()

function Get-Crc16([byte[]]$Bytes) {
    [uint16]$crc = 0xFFFF
    foreach ($value in $Bytes) {
        $crc = $crc -bxor $value
        for ($bit = 0; $bit -lt 8; $bit++) {
            if (($crc -band 1) -ne 0) {
                $crc = [uint16](($crc -shr 1) -bxor 0xA001)
            } else {
                $crc = [uint16]($crc -shr 1)
            }
        }
    }
    return $crc
}

function Send-ReadStatus([string]$DeviceId, [uint16]$MessageId) {
    [byte[]]$dev = [Text.Encoding]::ASCII.GetBytes($DeviceId)
    [byte[]]$body = [Text.Encoding]::ASCII.GetBytes("{}")
    [byte[]]$app = [byte[]]::new(18 + $dev.Length + $body.Length)
    $app[0] = 0xB1; $app[1] = 0x0F; $app[2] = 1; $app[3] = 0
    [BitConverter]::GetBytes([uint16]0x0034).CopyTo($app, 4)
    [BitConverter]::GetBytes($MessageId).CopyTo($app, 6)
    $app[10] = [byte]$dev.Length
    [BitConverter]::GetBytes([uint16]$body.Length).CopyTo($app, 12)
    [BitConverter]::GetBytes([uint32][DateTimeOffset]::UtcNow.ToUnixTimeSeconds()).CopyTo($app, 14)
    $dev.CopyTo($app, 18)
    $body.CopyTo($app, 18 + $dev.Length)

    [byte[]]$crcInput = @(1, (($app.Length -shr 8) -band 0xFF), ($app.Length -band 0xFF)) + $app
    [uint16]$crc = Get-Crc16 $crcInput
    [byte[]]$frame = [byte[]]::new(5 + $app.Length + 2)
    $frame[0] = 0xA5; $frame[1] = 0x5A; $frame[2] = 1
    $frame[3] = [byte](($app.Length -shr 8) -band 0xFF)
    $frame[4] = [byte]($app.Length -band 0xFF)
    $app.CopyTo($frame, 5)
    $frame[$frame.Length - 2] = [byte](($crc -shr 8) -band 0xFF)
    $frame[$frame.Length - 1] = [byte]($crc -band 0xFF)
    $serial.Write($frame, 0, $frame.Length)
}

function Write-Event([string]$Text) {
    $stamp = Get-Date -Format "HH:mm:ss.fff"
    $line = "[$stamp] $Text"
    Write-Host $line
    Add-Content -LiteralPath $script:logPath -Value $line -Encoding utf8
}

function Show-Payload([byte[]]$Data) {
    if ($Data.Length -ge 18 -and $Data[0] -eq 0xB1 -and $Data[1] -eq 0x0F) {
        $cmd = [BitConverter]::ToUInt16($Data, 4)
        $devLen = [int]$Data[10]
        $srcLen = [int]$Data[11]
        $bodyLen = [int][BitConverter]::ToUInt16($Data, 12)
        $offset = 18
        if (($Data[3] -band 0x08) -ne 0) { $offset += 44 }
        if ($offset + $devLen + $srcLen + $bodyLen -gt $Data.Length) { return }
        $dev = if ($devLen) { [Text.Encoding]::ASCII.GetString($Data, $offset, $devLen) } else { "" }
        $offset += $devLen
        $src = if ($srcLen) { [Text.Encoding]::ASCII.GetString($Data, $offset, $srcLen) } else { "" }
        $offset += $srcLen
        $body = if ($bodyLen) { [Text.Encoding]::UTF8.GetString($Data, $offset, $bodyLen) } else { "" }
        if ($cmd -eq 0x0002 -and $bodyLen -ge 18) {
            $layer = $Data[$offset + 10]
            $sendFail = [BitConverter]::ToUInt16($Data, $offset + 12)
            $queueFull = [BitConverter]::ToUInt16($Data, $offset + 14)
            $recoveries = [BitConverter]::ToUInt16($Data, $offset + 16)
            Write-Event "HEARTBEAT dev=$dev src=$src layer=$layer send_fail=$sendFail queue_full=$queueFull recoveries=$recoveries"
        } elseif ($cmd -eq 0x0006) {
            Write-Event "LOG $body"
        } else {
            Write-Event ("APP cmd=0x{0:X4} dev={1} src={2} body={3}" -f $cmd, $dev, $src, $body)
        }
    } else {
        $text = [Text.Encoding]::UTF8.GetString($Data)
        Write-Event "FRAME $text"
    }
}

$logDir = Join-Path $PSScriptRoot "logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$script:logPath = Join-Path $logDir ("mesh_serial_{0}.log" -f (Get-Date -Format "yyyyMMdd_HHmmss"))

try {
    $serial.Open()
    Write-Event "MONITOR_START port=$Port baud=$Baud seconds=$Seconds"
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    $probeTargets = @(
        "ROOT_B81F3FA9F404",
        "CAB_14C19F3949E8", "CAB_14C19F3A860C",
        "CAB_14C19FCEF124", "CAB_14C19FCEF164",
        "CAB_441BF6FE87AC", "CAB_441BF6FFC160",
        "CAB_ACA704E210BC", "CAB_ACA704E38AA0"
    )
    $nextProbe = [DateTime]::UtcNow
    $probeIndex = 0
    [uint16]$probeMessageId = 1
    $buffer = [byte[]]::new(4096)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($ActiveProbe -and [DateTime]::UtcNow -ge $nextProbe) {
            $target = $probeTargets[$probeIndex]
            Send-ReadStatus $target $probeMessageId
            Write-Event "PROBE READ_STATUS dev=$target msg=$probeMessageId"
            $probeMessageId++
            if ($probeMessageId -eq 0) { $probeMessageId = 1 }
            $probeIndex = ($probeIndex + 1) % $probeTargets.Count
            $nextProbe = [DateTime]::UtcNow.AddMilliseconds(650)
        }
        try { $count = $serial.Read($buffer, 0, $buffer.Length) } catch [System.TimeoutException] { continue }
        for ($i = 0; $i -lt $count; $i++) {
            [byte]$b = $buffer[$i]
            switch ($state) {
                0 { if ($b -eq 0xA5) { $state = 1 } elseif ($b -in 10,13) { if ($plain.Length) { Write-Event "PLAIN $plain"; $plain.Clear() | Out-Null } } elseif ($b -ge 32 -and $b -lt 127) { $plain.Append([char]$b) | Out-Null } }
                1 { if ($b -eq 0x5A) { $state = 2 } elseif ($b -ne 0xA5) { $state = 0 } }
                2 { $version = $b; $state = 3 }
                3 { $length = [int]$b -shl 8; $state = 4 }
                4 { $length += $b; $payload.Clear(); $crcBytes.Clear(); if ($length -gt 8192) { $state = 0 } elseif ($length -eq 0) { $state = 6 } else { $state = 5 } }
                5 { $payload.Add($b); if ($payload.Count -eq $length) { $state = 6 } }
                6 { $crcBytes.Add($b); $state = 7 }
                7 {
                    $crcBytes.Add($b)
                    [byte[]]$check = @($version, (($length -shr 8) -band 0xFF), ($length -band 0xFF)) + $payload.ToArray()
                    $expected = Get-Crc16 $check
                    $actual = ([int]$crcBytes[0] -shl 8) + $crcBytes[1]
                    if ($expected -eq $actual) { Show-Payload $payload.ToArray() } else { Write-Event "CRC_ERROR len=$length" }
                    $state = 0
                }
            }
        }
    }
    Write-Event "MONITOR_END log=$script:logPath"
} finally {
    if ($serial.IsOpen) { $serial.Close() }
    $serial.Dispose()
}
