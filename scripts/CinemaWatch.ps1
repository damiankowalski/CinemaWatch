<#
.SYNOPSIS
    CinemaWatch - monitor wolnych miejsc w Cinema City z powiadomieniem mailowym.

.DESCRIPTION
    Dla kazdej daty z -Dates pobiera repertuar kina, wybiera seanse filmu spelniajace
    warunki formatu (domyslnie IMAX + 2D + napisy, bez dubbingu) i zakresu godzin,
    a nastepnie sprawdza zajetosc sali. Jesli w rzedach z -Rows pojawia sie wolne miejsca
    (min. -MinAdjacent obok siebie), ktorych nie bylo w poprzednim sprawdzeniu, wysyla maila.

    Skrypt tylko CZYTA dane (repertuar, uklad sali, status miejsc). Nie blokuje miejsc
    i niczego nie kupuje - zakup robisz recznie z linku w mailu.

    Zrodla danych:
      repertuar : www.cinema-city.pl/pl/data-api-service/v1/quickbook/10103/film-events/...
      seans     : tickets.cinema-city.pl/api/presentations/{id}
      uklad sali: tickets.cinema-city.pl/api/seats/seatplanV2   (POST, body '{}')
      zajetosc  : tickets.cinema-city.pl/api/seats/seats-statusV2 (lista WOLNYCH miejsc)

.EXAMPLE
    .\CinemaWatch.ps1 -TestMail
    Wysyla testowego maila (sprawdzenie konfiguracji Gmail).

.EXAMPLE
    .\CinemaWatch.ps1 -DryRun -Dates 2026-12-16 -Rows 4,5 -TimeFrom 15:00 -TimeTo 17:00
    Pokazuje, co zostaloby wyslane, bez wysylania maila i bez zapisu stanu.

.NOTES
    Wymagania: Windows PowerShell 5.1 lub PowerShell 7+.
    Dane logowania Gmail (adres + haslo aplikacji) zapisane przez Export-Clixml w $CredPath.
#>
[CmdletBinding()]
param(
    # --- Czego szukamy -------------------------------------------------------------
    [string[]] $Dates       = @('2026-12-15', '2026-12-16'),  # yyyy-MM-dd
    [string]   $TimeFrom    = '15:00',                        # poczatek seansu od (wlacznie)
    [string]   $TimeTo      = '21:00',                        # poczatek seansu do (wlacznie)
    [string[]] $Rows        = @('7', '8', '9', '10'),         # numery rzedow (1 = najblizej ekranu)
    [int]      $MinAdjacent = 1,                              # ile wolnych miejsc obok siebie w rzedzie
    [switch]   $IncludeWheelchair,                            # uwzglednij miejsca dla niepelnosprawnych

    # --- Film / kino / format ------------------------------------------------------
    [string]   $FilmId      = '8105s2r',                      # Diuna: Czesc trzecia
    [string]   $CinemaId    = '1060',                         # Cinema City Sadyba
    [string[]] $RequireAttr = @('imax', '2d', 'subbed'),
    [string[]] $ExcludeAttr = @('dubbed', '3d'),

    # --- Powiadomienia i pliki -----------------------------------------------------
    [string]   $MailTo      = 'you@example.com',
    [string]   $SmtpServer  = 'smtp.gmail.com',
    [int]      $SmtpPort    = 587,
    [string]   $DataDir     = (Join-Path $env:LOCALAPPDATA 'CinemaWatch'),
    [int]      $ErrorMailEveryHours = 6,

    # --- Tryby ---------------------------------------------------------------------
    [switch]   $DryRun,
    [switch]   $TestMail
)

$ErrorActionPreference = 'Stop'

# '-Rows 7,8' z Harmonogramu zadan (-File) przychodzi jako jeden string - rozbijamy po przecinkach.
$Dates = @($Dates | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$Rows  = @($Rows  | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$CredPath  = Join-Path $DataDir 'gmail.xml'
$StatePath = Join-Path $DataDir 'state.json'
$LogPath   = Join-Path $DataDir 'watch.log'
$ErrorFlag = Join-Path $DataDir 'last-error-mail.txt'
$Api       = 'https://tickets.cinema-city.pl/api'
$PlCulture = [Globalization.CultureInfo]'pl-PL'

if (-not (Test-Path $DataDir)) { New-Item $DataDir -ItemType Directory -Force | Out-Null }
if ((Test-Path $LogPath) -and (Get-Item $LogPath).Length -gt 1MB) { Move-Item $LogPath "$LogPath.old" -Force }

#region Helpers -------------------------------------------------------------------------

function Write-Log([string]$Message) {
    $line = '{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $Message
    Add-Content -Path $LogPath -Value $line -Encoding UTF8
    Write-Host $line
}

# Pelny lancuch wyjatkow - SmtpException sam w sobie mowi tylko "Wysylanie poczty nie powiodlo sie".
function Get-ErrorText($ErrorRecord) {
    $parts = @()
    $e = $ErrorRecord.Exception
    while ($e) {
        $m = $e.Message.Trim()
        if ($parts -notcontains $m) { $parts += $m }
        $e = $e.InnerException
    }
    $parts -join ' -> '
}

function Send-Notification([string]$Subject, [string]$Body) {
    if (-not (Test-Path $CredPath)) { throw "Brak pliku z danymi Gmail: $CredPath (zobacz instrukcje konfiguracji)." }
    $cred = Import-Clixml $CredPath
    $msg  = New-Object System.Net.Mail.MailMessage
    $smtp = New-Object System.Net.Mail.SmtpClient($SmtpServer, $SmtpPort)
    try {
        $msg.From            = $cred.UserName
        $msg.To.Add($MailTo)
        $msg.Subject         = $Subject
        $msg.Body            = $Body
        $msg.SubjectEncoding = [Text.Encoding]::UTF8
        $msg.BodyEncoding    = [Text.Encoding]::UTF8
        $smtp.EnableSsl      = $true
        $smtp.Credentials    = $cred.GetNetworkCredential()
        $smtp.Send($msg)
    }
    finally { $msg.Dispose(); $smtp.Dispose() }
}

# Jedna sesja (cookies) i jeden identyfikator klienta na przebieg - tak jak przegladarka.
$Session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$Headers = @{ uuid = [guid]::NewGuid().ToString() }

function Invoke-CC([string]$Url, [string]$Method = 'Get') {
    $p = @{ Uri = $Url; Method = $Method; Headers = $Headers; WebSession = $Session; UseBasicParsing = $true; TimeoutSec = 30 }
    if ($Method -eq 'Post') { $p.Body = '{}'; $p.ContentType = 'application/json' }
    $r = Invoke-WebRequest @p
    # Jawne dekodowanie UTF-8 (PS 5.1 bez charset w naglowku psuje polskie znaki).
    [Text.Encoding]::UTF8.GetString($r.RawContentStream.ToArray()) | ConvertFrom-Json
}

# PS 7 zamienia daty ISO z JSON na [datetime], PS 5.1 zostawia string - obslugujemy oba.
function Get-EventTime($Ev) {
    $v = $Ev.eventDateTime
    if ($v -is [datetime]) { return $v }
    [datetime]::ParseExact([string]$v, "yyyy-MM-dd'T'HH:mm:ss", [Globalization.CultureInfo]::InvariantCulture)
}

# Uklad sali -> lista miejsc. Klucz statusu ma postac {sekcja}_{pozycja}_{rzad}
# (np. 1_11_8), gdzie pozycja to kolejny indeks miejsca w rzedzie od lewej strony.
function ConvertTo-SeatList($Plan) {
    $list = New-Object System.Collections.Generic.List[object]
    foreach ($sec in $Plan.S.PSObject.Properties) {
        foreach ($grp in $sec.Value.G.PSObject.Properties) {
            foreach ($row in $grp.Value.R.PSObject.Properties) {
                foreach ($seat in $row.Value.S.PSObject.Properties) {
                    $list.Add([pscustomobject]@{
                        Key        = ('{0}_{1}_{2}' -f $sec.Name, $seat.Name, $row.Name)
                        Row        = [string]$row.Value.n
                        Seat       = [string]$seat.Value.n
                        Pos        = [int]$seat.Name
                        Wheelchair = [bool]$seat.Value.hc
                    })
                }
            }
        }
    }
    return $list
}

# Z wolnych miejsc jednego rzedu zostawia tylko te, ktore tworza blok >= $MinAdjacent.
function Select-AdjacentSeats($RowSeats) {
    $result = New-Object System.Collections.Generic.List[object]
    $run    = New-Object System.Collections.Generic.List[object]
    foreach ($s in ($RowSeats | Sort-Object Pos)) {
        if ($run.Count -gt 0 -and $s.Pos -ne $run[$run.Count - 1].Pos + 1) {
            if ($run.Count -ge $MinAdjacent) { $result.AddRange($run) }
            $run.Clear()
        }
        $run.Add($s)
    }
    if ($run.Count -ge $MinAdjacent) { $result.AddRange($run) }
    return $result
}

#endregion

#region Main ----------------------------------------------------------------------------

if ($TestMail) {
    try {
        Send-Notification -Subject 'CinemaWatch - test' -Body "Test powiadomien CinemaWatch: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
        Write-Log "Wyslano testowego maila do $MailTo."
    }
    catch {
        Write-Log "BLAD wysylki testowej: $(Get-ErrorText $_)"
        exit 1
    }
    return
}

# Stan: dla kazdego seansu zbior kluczy miejsc spelniajacych warunki w ostatnim sprawdzeniu.
$State = @{}
if (Test-Path $StatePath) {
    $raw = Get-Content $StatePath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($raw) { foreach ($p in $raw.PSObject.Properties) { $State[$p.Name] = @($p.Value) } }
}

$from      = [timespan]$TimeFrom
$to        = [timespan]$TimeTo
$planCache = @{}
$findings  = New-Object System.Collections.Generic.List[object]
$errors    = New-Object System.Collections.Generic.List[string]

foreach ($date in $Dates) {
    try {
        $rep = Invoke-CC "https://www.cinema-city.pl/pl/data-api-service/v1/quickbook/10103/film-events/in-cinema/$CinemaId/at-date/$($date)?attr=&lang=pl_PL"
        $film = $rep.body.films | Where-Object { $_.id -eq $FilmId } | Select-Object -First 1
        $events = @($rep.body.events | Where-Object {
            $e = $_
            $t = (Get-EventTime $e).TimeOfDay
            $e.filmId -eq $FilmId -and
            $t -ge $from -and $t -le $to -and
            -not ($RequireAttr | Where-Object { $e.attributeIds -notcontains $_ }) -and
            -not ($ExcludeAttr | Where-Object { $e.attributeIds -contains $_ })
        })
        Write-Log ("{0}: {1} pasujacych seansow" -f $date, $events.Count)
    }
    catch { $errors.Add("Repertuar $($date): $(Get-ErrorText $_)"); continue }

    foreach ($ev in $events) {
        $id   = [string]$ev.id
        $when = Get-EventTime $ev
        try {
            $current = @()
            $total = 0; $freeTotal = 0
            if (-not $ev.soldOut) {
                $pres = (Invoke-CC "$Api/presentations/$($id)?referralMiniSiteId=0").presentation

                $planKey = '{0}_{1}' -f $pres.venueId, $pres.seatplanId
                if (-not $planCache.ContainsKey($planKey)) {
                    $plan = Invoke-CC "$Api/seats/seatplanV2?venueId=$($pres.venueId)&seatplanId=$($pres.seatplanId)" -Method Post
                    $planCache[$planKey] = ConvertTo-SeatList $plan
                }
                $seats = $planCache[$planKey]

                $status = Invoke-CC "$Api/seats/seats-statusV2?presentationId=$id&venueTypeId=$($pres.venueTypeId)&isReserved=$($pres.isReserved)"
                $free = New-Object 'System.Collections.Generic.HashSet[string]'
                foreach ($p in $status.seats.PSObject.Properties) { if ([int]$p.Value -eq 0) { [void]$free.Add($p.Name) } }

                # Statystyka sali liczy tylko zwykle miejsca. Miejsca dla wozkow (np. 1N/2N w rzedzie 12)
                # API zwraca jako wolne nawet wtedy, gdy strona pokazuje seans jako wyprzedany.
                $regular   = @($seats | Where-Object { -not $_.Wheelchair })
                $total     = $regular.Count
                $freeTotal = @($regular | Where-Object { $free.Contains($_.Key) }).Count
                $candidates = $seats | Where-Object {
                    $free.Contains($_.Key) -and $Rows -contains $_.Row -and ($IncludeWheelchair -or -not $_.Wheelchair)
                }
                foreach ($grp in ($candidates | Group-Object Row)) {
                    $current += @(Select-AdjacentSeats $grp.Group)
                }
            }

            $currentKeys  = @($current | ForEach-Object { $_.Key })
            $previousKeys = @()
            if ($State.ContainsKey($id)) { $previousKeys = @($State[$id]) }
            $newKeys = @($currentKeys | Where-Object { $previousKeys -notcontains $_ })

            if ($ev.soldOut) {
                Write-Log ("  {0:HH:mm} [{1}] {2}: WYPRZEDANY" -f $when, $id, $ev.auditorium)
            }
            else {
                Write-Log ("  {0:HH:mm} [{1}] {2}: {3} wolnych w wybranych rzedach, nowych: {4} (sala: {5}/{6})" -f `
                    $when, $id, $ev.auditorium, $currentKeys.Count, $newKeys.Count, $freeTotal, $total)
            }

            if ($newKeys.Count -gt 0) {
                $findings.Add([pscustomobject]@{
                    Event     = $ev
                    When      = $when
                    FilmName  = if ($film) { $film.name } else { $FilmId }
                    Seats     = $current
                    FreeTotal = $freeTotal
                    Total     = $total
                })
            }
            $State[$id] = $currentKeys
        }
        catch { $errors.Add("Seans $id ($($when.ToString('dd.MM HH:mm'))): $(Get-ErrorText $_)") }
    }
}

# --- Powiadomienie o wolnych miejscach --------------------------------------------------
if ($findings.Count -gt 0) {
    $sb = New-Object System.Text.StringBuilder
    foreach ($f in ($findings | Sort-Object When)) {
        $ev   = $f.Event
        $lang = 'oryg. {0}' -f (@($ev.languages.original) -join '/').ToUpper()
        if (@($ev.languages.subtitles).Count) { $lang += ', napisy ' + (@($ev.languages.subtitles) -join '/').ToUpper() }
        [void]$sb.AppendLine(('{0} - {1}' -f $f.FilmName, $f.When.ToString('dddd dd.MM.yyyy, HH:mm', $PlCulture)))
        [void]$sb.AppendLine(('Sala: {0} | {1}' -f $ev.auditorium, $lang))
        [void]$sb.AppendLine('Wolne miejsca w wybranych rzedach:')
        foreach ($grp in ($f.Seats | Group-Object Row | Sort-Object { [int]($_.Name -replace '\D', '') })) {
            $labels = ($grp.Group | Sort-Object Pos | ForEach-Object { $_.Seat }) -join ', '
            [void]$sb.AppendLine(('  Rzad {0}: {1}' -f $grp.Name, $labels))
        }
        if ($f.Total -gt 0) {
            [void]$sb.AppendLine(('Cala sala: {0}/{1} wolnych ({2:P0})' -f $f.FreeTotal, $f.Total, ($f.FreeTotal / $f.Total)))
        }
        [void]$sb.AppendLine("Kup: $($ev.bookingLink)")
        [void]$sb.AppendLine()
    }
    [void]$sb.AppendLine('Numeracja miejsc jak na mapie sali (od lewej). Rzad 1 jest najblizej ekranu.')

    $first   = ($findings | Sort-Object When | Select-Object -First 1)
    $subject = 'Wolne miejsca: {0} - {1}' -f $first.FilmName, $first.When.ToString('dd.MM HH:mm')
    if ($findings.Count -gt 1) { $subject += " (+$($findings.Count - 1) seans/e)" }

    if ($DryRun) {
        Write-Host "`n[DryRun] Temat: $subject`n$($sb.ToString())"
    }
    else {
        try {
            Send-Notification -Subject $subject -Body $sb.ToString()
            Write-Log "Wyslano powiadomienie: $subject"
        }
        catch {
            $errors.Add("Wysylka maila: $(Get-ErrorText $_)")
            # Nie zapisujemy stanu dla tych seansow, zeby przy nastepnym przebiegu sprobowac ponownie.
            foreach ($f in $findings) { [void]$State.Remove([string]$f.Event.id) }
        }
    }
}

# --- Bledy: log + mail najwyzej raz na $ErrorMailEveryHours godzin ----------------------
if ($errors.Count -gt 0) {
    foreach ($e in $errors) { Write-Log "BLAD: $e" }
    $due = -not (Test-Path $ErrorFlag) -or (Get-Item $ErrorFlag).LastWriteTime -lt (Get-Date).AddHours(-$ErrorMailEveryHours)
    if ($due -and -not $DryRun) {
        try {
            Send-Notification -Subject 'CinemaWatch - problem z monitorowaniem' `
                -Body ("Ostatni przebieg zakonczyl sie bledami:`n`n" + ($errors -join "`n") + "`n`nLog: $LogPath")
            Set-Content $ErrorFlag (Get-Date -Format o)
        }
        catch { Write-Log "BLAD: nie udalo sie wyslac maila o bledzie: $(Get-ErrorText $_)" }
    }
}

if (-not $DryRun) {
    $State | ConvertTo-Json -Depth 3 | Set-Content $StatePath -Encoding UTF8
}

#endregion
