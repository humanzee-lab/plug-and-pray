<#
.SYNOPSIS
  Reboot a Betaflight/STM32 flight controller from normal (VCP) mode into DFU
  (ROM bootloader) mode over its serial port. This is the "kick" that the driver
  fixer performs so it can then bind WinUSB to the DFU interface.

.NOTES
  Two methods, tried in order:
    1. MSP_SET_REBOOT (command 68) with payload 1 = MSP_REBOOT_BOOTLOADER_ROM.
       Deterministic, supported on Betaflight 4.x. Leaves no side effects if ignored.
    2. CLI "bl" command (send '#' to enter CLI, then 'bl'). Universal fallback.

  A board in DFU/bootloader mode is inert — motors cannot spin. Replugging USB
  (power-cycling the board) always returns it to normal firmware mode.

  MSP v1 frame to FC:  '$' 'M' '<' <size> <cmd> <data...> <crc>
  crc = XOR of size, cmd, and every data byte.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PortName,
    [int] $BaudRate = 115200,
    [switch] $UseCli   # skip MSP, go straight to the CLI 'bl' method
)

function New-MspFrame {
    param([byte] $Command, [byte[]] $Data = @())
    $size = [byte] $Data.Length
    $crc = $size -bxor $Command
    foreach ($b in $Data) { $crc = $crc -bxor $b }
    return ,([byte[]]( 0x24, 0x4D, 0x3C, $size, $Command, $Data + $crc )) `
        | ForEach-Object { $_ }   # flatten
}

# Build MSP_SET_REBOOT(bootloader) explicitly to avoid array-flattening surprises
$MSP_SET_REBOOT = 0x44          # 68
$MSP_REBOOT_BOOTLOADER_ROM = 0x01
$size = 0x01
$crc  = $size -bxor $MSP_SET_REBOOT -bxor $MSP_REBOOT_BOOTLOADER_ROM
$mspRebootFrame = [byte[]]( 0x24, 0x4D, 0x3C, $size, $MSP_SET_REBOOT, $MSP_REBOOT_BOOTLOADER_ROM, $crc )

Write-Host "Opening $PortName at $BaudRate..."
$port = New-Object System.IO.Ports.SerialPort($PortName, $BaudRate, 'None', 8, 'One')
$port.ReadTimeout  = 800
$port.WriteTimeout = 800
try {
    $port.Open()
} catch {
    Write-Error "Could not open ${PortName}: $($_.Exception.Message)"
    exit 2
}

try {
    if (-not $UseCli) {
        $hex = ($mspRebootFrame | ForEach-Object { $_.ToString('X2') }) -join ' '
        Write-Host "Sending MSP_SET_REBOOT->bootloader: $hex"
        $port.Write($mspRebootFrame, 0, $mspRebootFrame.Length)
        Start-Sleep -Milliseconds 300
    } else {
        Write-Host "Entering CLI (#) and sending 'bl'..."
        $port.NewLine = "`r"
        $port.Write("#")
        Start-Sleep -Milliseconds 400
        try { $banner = $port.ReadExisting() } catch { $banner = '' }
        if ($banner) { Write-Host "CLI responded ($($banner.Length) bytes)" }
        $port.Write("bl`r")
        Start-Sleep -Milliseconds 300
    }
} finally {
    try { $port.Close() } catch {}
    $port.Dispose()
}
Write-Host "Kick sent. Board should re-enumerate as DFU within ~2s."
