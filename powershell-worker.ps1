# powershell-worker.ps1
# Persistent background worker for InazumaPwsh external PowerShell sessions.
param(
    [Parameter(Mandatory = $false, Position = 0)]
    [string]$PipeName
)

if (-not $PipeName -and $args.Count -gt 0) {
    $PipeName = $args[0]
}

if (-not $PipeName) {
    [Console]::Error.WriteLine("Error: PipeName parameter is required.")
    exit 1
}

$ErrorActionPreference = "Stop"

if ($PSVersionTable.PSVersion.Major -lt 7) {
    [Console]::Error.WriteLine("PowerShell 7 or higher is required. Found version: $($PSVersionTable.PSVersion)")
    exit 1
}

$pipe = [System.IO.Pipes.NamedPipeClientStream]::new(".", $PipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
try {
    $pipe.Connect(15000)
} catch {
    [Console]::Error.WriteLine("Failed to connect to named pipe: $_")
    exit 1
}

$reader = [System.IO.StreamReader]::new($pipe, [System.Text.UTF8Encoding]::new($false))
$writer = [System.IO.StreamWriter]::new($pipe, [System.Text.UTF8Encoding]::new($false))
$writer.AutoFlush = $true
$writer.NewLine = "`n"

$initialSessionState = [System.Management.Automation.Runspaces.InitialSessionState]::CreateDefault()
$initialSessionState.ExecutionPolicy = [Microsoft.PowerShell.ExecutionPolicy]::RemoteSigned
$initialSessionState.Variables.Add([System.Management.Automation.Runspaces.SessionStateVariableEntry]::new("ConfirmPreference", [System.Management.Automation.ConfirmImpact]::None, ""))
$initialSessionState.Variables.Add([System.Management.Automation.Runspaces.SessionStateVariableEntry]::new("ProgressPreference", [System.Management.Automation.ActionPreference]::SilentlyContinue, ""))

$runspace = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace($initialSessionState)
$runspace.ThreadOptions = [System.Management.Automation.Runspaces.PSThreadOptions]::ReuseThread
$runspace.Open()
try {
    [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
    [Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
} catch {}
$runspace.SessionStateProxy.SetVariable('OutputEncoding', [System.Text.UTF8Encoding]::new($false))

function Close-PythonStdin([System.Management.Automation.Language.ScriptBlockAst]$syntax, [string]$script, $rs) {
    $declaredFunctions = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $fnNodes = $syntax.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)
    if ($null -ne $fnNodes) {
        foreach ($fn in $fnNodes) {
            if ($fn.Name) { [void]$declaredFunctions.Add($fn.Name) }
        }
    }

    $offsets = [System.Collections.Generic.List[int]]::new()
    $pipeNodes = $syntax.FindAll({ $args[0] -is [System.Management.Automation.Language.PipelineAst] }, $true)
    if ($null -ne $pipeNodes) {
        foreach ($p in $pipeNodes) {
            $first = $p.PipelineElements[0]
            if ($first -is [System.Management.Automation.Language.CommandAst]) {
                $cmdName = $first.GetCommandName()
                if ($cmdName) {
                    $baseName = [System.IO.Path]::GetFileName($cmdName)
                    if ($baseName.EndsWith(".exe", [System.StringComparison]::OrdinalIgnoreCase)) {
                        $baseName = $baseName.Substring(0, $baseName.Length - 4)
                    }
                    if ($baseName -match '^(?:py|python(?:\d+(?:\.\d+)?)?)$') {
                        if (-not $declaredFunctions.Contains($cmdName)) {
                            $cmdInfo = $null
                            try {
                                $cmdInfo = $rs.SessionStateProxy.InvokeCommand.GetCommand($cmdName, [System.Management.Automation.CommandTypes]::All)
                            } catch {}
                            if ($cmdInfo -and $cmdInfo.CommandType -eq [System.Management.Automation.CommandTypes]::Application) {
                                $offsets.Add($p.Extent.StartOffset)
                            }
                        }
                    }
                }
            }
        }
    }

    $offsets.Sort()
    $offsets.Reverse()
    foreach ($offset in $offsets) {
        $script = $script.Insert($offset, '$null | ')
    }
    return $script
}

$readyPayload = ConvertTo-Json @{
    type = "ready"
    version = $PSVersionTable.PSVersion.ToString()
} -Compress
$writer.WriteLine($readyPayload)

$pendingRead = $reader.ReadLineAsync()

try {
    while ($true) {
        $rawMsg = $pendingRead.GetAwaiter().GetResult()
        if ($null -eq $rawMsg) { break }
        $pendingRead = $reader.ReadLineAsync()

        $request = $null
        try {
            $request = ConvertFrom-Json $rawMsg
        } catch {
            continue
        }

        $action = $request.action
        $id = $request.id

        if ($action -eq "reset") {
            try {
                $runspace.Close()
                $runspace.Dispose()
            } catch {}
            $runspace = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace($initialSessionState)
            $runspace.ThreadOptions = [System.Management.Automation.Runspaces.PSThreadOptions]::ReuseThread
            $runspace.Open()

            $resObj = @{
                type = "result"
                id = $id
                status = "success"
                dataIsString = $true
                dataString = "PowerShell session has been reset successfully."
                errors = @()
                warnings = @()
                infoOutputs = @()
                exitCode = 0
            }
            $writer.WriteLine((ConvertTo-Json $resObj -Compress))
            continue
        }

        if ($action -eq "execute") {
            $script = $request.script
            $workingDirectory = $request.workingDirectory

            $parseErrors = $null
            $tokens = $null
            $syntax = [System.Management.Automation.Language.Parser]::ParseInput($script, [ref]$tokens, [ref]$parseErrors)
            if ($parseErrors -and $parseErrors.Count -gt 0) {
                $errList = @($parseErrors | ForEach-Object {
                    "[Line $($_.Extent.StartLineNumber), Col $($_.Extent.StartColumnNumber)] $($_.Message)"
                })
                $resObj = @{
                    type = "result"
                    id = $id
                    status = "error"
                    dataIsString = $false
                    dataJson = $null
                    dataString = $null
                    errors = $errList
                    warnings = @()
                    infoOutputs = @()
                    exitCode = 0
                    allStrings = $false
                    outputStrings = @()
                }
                $writer.WriteLine((ConvertTo-Json $resObj -Compress))
                continue
            }

            if ($runspace.RunspaceStateInfo.State -ne [System.Management.Automation.Runspaces.RunspaceState]::Opened) {
                try { $runspace.Close(); $runspace.Dispose() } catch {}
                $runspace = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace($initialSessionState)
                $runspace.ThreadOptions = [System.Management.Automation.Runspaces.PSThreadOptions]::ReuseThread
                $runspace.Open()
            }

            $prevLoc = $null
            if ($workingDirectory) {
                try {
                    $prevLoc = $runspace.SessionStateProxy.Path.CurrentLocation.Path
                    $runspace.SessionStateProxy.Path.SetLocation($workingDirectory)
                } catch {
                    $resObj = @{
                        type = "result"
                        id = $id
                        status = "error"
                        dataIsString = $false
                        dataJson = $null
                        dataString = $null
                        errors = @($_.Exception.Message)
                        warnings = @()
                        infoOutputs = @()
                        exitCode = 0
                        allStrings = $false
                        outputStrings = @()
                    }
                    $writer.WriteLine((ConvertTo-Json $resObj -Compress))
                    continue
                }
            }

            $runspace.SessionStateProxy.SetVariable("LASTEXITCODE", 0)

            $modScript = Close-PythonStdin $syntax $script $runspace

            $ps = [System.Management.Automation.PowerShell]::Create()
            $ps.Runspace = $runspace
            $null = $ps.AddScript($modScript)
            $outputCollection = [System.Management.Automation.PSDataCollection[psobject]]::new()

            $asyncResult = $ps.BeginInvoke[psobject, psobject]($null, $outputCollection)

            while (-not $asyncResult.AsyncWaitHandle.WaitOne(50)) {
                if ($pendingRead.IsCompleted) {
                    $stopMsg = $pendingRead.GetAwaiter().GetResult()
                    if ($null -eq $stopMsg) { break }
                    $pendingRead = $reader.ReadLineAsync()
                    try {
                        $parsedStop = ConvertFrom-Json $stopMsg
                        if ($parsedStop.action -eq "stop" -and $parsedStop.id -eq $id) {
                            try { $ps.Stop() } catch {}
                        }
                    } catch {}
                }
            }

            $stopped = $false
            try {
                $null = $ps.EndInvoke($asyncResult)
            } catch [System.Management.Automation.PipelineStoppedException] {
                $stopped = $true
            } catch {
            }

            if ($ps.InvocationStateInfo.State -eq [System.Management.Automation.PSInvocationState]::Stopped) {
                $stopped = $true
            }

            if ($prevLoc -and $runspace.RunspaceStateInfo.State -eq [System.Management.Automation.Runspaces.RunspaceState]::Opened) {
                try { $runspace.SessionStateProxy.Path.SetLocation($prevLoc) } catch {}
            }

            $errors = [System.Collections.Generic.List[string]]::new()
            foreach ($err in $ps.Streams.Error) {
                if ($null -eq $err) { continue }
                $msg = if ($err.Exception -and $err.Exception.Message) { $err.Exception.Message } else { $err.ToString() }
                if (-not [string]::IsNullOrWhiteSpace($err.ScriptStackTrace)) {
                    $msg += " (at $($err.ScriptStackTrace.Trim()))"
                }
                $errors.Add($msg)
            }

            $warnings = [System.Collections.Generic.List[string]]::new()
            foreach ($warn in $ps.Streams.Warning) {
                if ($null -eq $warn) { continue }
                if (-not [string]::IsNullOrWhiteSpace($warn.Message)) {
                    $warnings.Add($warn.Message)
                }
            }

            $infoOutputs = [System.Collections.Generic.List[string]]::new()
            foreach ($info in $ps.Streams.Information) {
                if ($null -ne $info -and $null -ne $info.MessageData) {
                    $infoOutputs.Add($info.MessageData.ToString())
                }
            }

            $exitCodeObj = $runspace.SessionStateProxy.GetVariable("LASTEXITCODE")
            $exitCode = 0
            if ($null -ne $exitCodeObj) {
                try { $exitCode = [int]$exitCodeObj } catch { $exitCode = 0 }
            }
            if ($exitCode -ne 0) {
                $errors.Add("Native command exited with code $exitCode.")
            }

            # Redirected native stderr (2>&1) arrives as ErrorRecord; keep only its text instead of the full object graph.
            $rawOutputCollection = $outputCollection
            $outputCollection = [System.Collections.Generic.List[psobject]]::new()
            foreach ($item in $rawOutputCollection) {
                if ($item -is [System.Management.Automation.ErrorRecord]) {
                    $outputCollection.Add([psobject]$item.ToString())
                } else {
                    $outputCollection.Add($item)
                }
            }

            $allStrings = $true
            $outputStrings = [System.Collections.Generic.List[string]]::new()
            foreach ($item in $outputCollection) {
                if ($null -ne $item) {
                    if ($item.BaseObject -isnot [string]) {
                        $allStrings = $false
                    }
                    $outputStrings.Add($item.ToString())
                }
            }

            $dataIsString = $false
            $dataString = $null
            $dataJson = $null

            if ($outputCollection.Count -eq 0) {
                if ($infoOutputs.Count -eq 1) {
                    $dataIsString = $true
                    $dataString = $infoOutputs[0]
                } elseif ($infoOutputs.Count -gt 1) {
                    $dataIsString = $true
                    $dataString = $infoOutputs -join "`r`n"
                }
            } elseif ($outputCollection.Count -eq 1 -and $outputCollection[0].BaseObject -is [string]) {
                $dataIsString = $true
                $dataString = [string]$outputCollection[0].BaseObject
            } else {
                # \u8907\u96d1\u306a.NET\u30aa\u30d6\u30b8\u30a7\u30af\u30c8\u3092\u6df1\u3044JSON\u306b\u3059\u308b\u3068\u6570MB\u3092\u8d85\u3048\u308b\u305f\u3081\u3001\u8868\u793a\u30c6\u30ad\u30b9\u30c8\u3078\u5909\u63db\u3059\u308b
                $plainData = $true
                foreach ($item in $outputCollection) {
                    $base = if ($null -ne $item) { $item.PSObject.BaseObject } else { $null }
                    if ($null -eq $base) { continue }
                    $t = $base.GetType()
                    if ($t.IsPrimitive -or $t.IsEnum -or $base -is [string] -or $base -is [decimal] -or $base -is [datetime] -or
                        $base -is [datetimeoffset] -or $base -is [timespan] -or $base -is [guid] -or $base -is [System.Management.Automation.PSCustomObject] -or
                        $base -is [System.Collections.IDictionary] -or $base -is [object[]]) { continue }
                    $plainData = $false
                    break
                }

                if (-not $plainData) {
                    try {
                        $textPs = [System.Management.Automation.PowerShell]::Create()
                        $textPs.Runspace = $runspace
                        $null = $textPs.AddCommand("Out-String").AddParameter("Width", 200)
                        $textRes = $textPs.Invoke($outputCollection)
                        $dataIsString = $true
                        $dataString = (($textRes | ForEach-Object { $_.ToString() }) -join "").TrimEnd()
                        $textPs.Dispose()
                    } catch {
                        $dataIsString = $true
                        $dataString = (($outputStrings) -join "`n")
                    }
                } else {
                try {
                    $jsonPs = [System.Management.Automation.PowerShell]::Create()
                    $jsonPs.Runspace = $runspace
                    $null = $jsonPs.AddCommand("ConvertTo-Json").AddParameter("Depth", 3).AddParameter("Compress", $true)
                    if ($outputCollection.Count -eq 1) {
                        $null = $jsonPs.AddParameter("InputObject", $outputCollection[0])
                    } else {
                        $null = $jsonPs.AddParameter("InputObject", $outputCollection)
                    }
                    $jsonRes = $jsonPs.Invoke()
                    if ($jsonRes -and $jsonRes.Count -gt 0) {
                        $dataJson = ($jsonRes | ForEach-Object { $_.ToString() }) -join "`n"
                    }
                    $jsonPs.Dispose()
                } catch {
                    $dataJson = $null
                }

                if ($null -eq $dataJson) {
                    if ($outputCollection.Count -eq 1) {
                        $dataIsString = $true
                        $dataString = if ($outputCollection[0]) { $outputCollection[0].ToString() } else { "" }
                    } else {
                        $dataJson = ConvertTo-Json @($outputCollection | ForEach-Object { $_.ToString() }) -Compress
                    }
                }
                }
            }

            try { $ps.Dispose(); $rawOutputCollection.Dispose() } catch {}

            $resObj = @{
                type = "result"
                id = $id
                status = if ($errors.Count -gt 0) { "error" } else { "success" }
                stopped = $stopped
                exitCode = $exitCode
                errors = @($errors)
                warnings = @($warnings)
                infoOutputs = @($infoOutputs)
                allStrings = $allStrings
                outputStrings = @($outputStrings)
                dataIsString = $dataIsString
                dataString = $dataString
                dataJson = $dataJson
            }

            $writer.WriteLine((ConvertTo-Json $resObj -Compress))
        }
    }
} finally {
    try { $runspace.Close(); $runspace.Dispose() } catch {}
    try { $reader.Dispose(); $writer.Dispose(); $pipe.Dispose() } catch {}
}



