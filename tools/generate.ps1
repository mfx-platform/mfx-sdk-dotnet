# The models, generated from the public openapi.yaml.
#
#   pwsh tools/generate.ps1
#
# spec/openapi.yaml is the Python SDK's public cut, copied as it is: .NET derives from the same contract
# Python does. The output is committed; CI regenerates it and fails on any difference ("no drift").
#
# The models are public, in MfxHub.Models, and .NET-named: after NSwag, every identifier the
# file declares is renamed to .NET style (Same_login_terminals is SameLoginTerminals, LOGGING_IN is
# LoggingIn). Only identifiers are renamed, never a string literal or a comment, so each wire name stays
# where NSwag wrote it, in [JsonPropertyName]; each enum member gains [JsonStringEnumMemberName] with its
# wire name, because System.Text.Json does not read [EnumMember]. The type names stay the contract's
# (TenantTerminal), which are the names Python's surface.json uses. Each type made from a schema of
# components.schemas carries that schema's key in [ContractSchema], which docs/sdkref.json reads.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# A declared name in .NET style: each underscore-separated part capitalised, and a part written all in
# capitals (LOGGING, IN) written as a word. A name that would start with a digit keeps NSwag's underscore.
function ConvertTo-DotnetName([string] $name) {
    $parts = $name.Split('_', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object {
        if ($_ -cmatch '^[A-Z]{2,}$') { $_.Substring(0, 1) + $_.Substring(1).ToLowerInvariant() }
        else { $_.Substring(0, 1).ToUpperInvariant() + $_.Substring(1) }
    }
    $out = $parts -join ''
    if ($out -match '^[0-9]') { $out = '_' + $out }
    return $out
}

# NSwag's output in .NET style. Renames are by identifier token: string literals and comments are matched
# first and kept as they are. A rename that would make two types one is refused here; one that makes two
# members of a type one is a compile error, which CI's build reports.
function ConvertTo-DotnetStyle([string] $body) {
    $declared = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $types = [regex]::Matches($body, '(?m)^\s*public (?:partial class|enum) (\w+)') | ForEach-Object { $_.Groups[1].Value }
    $members = @(
        [regex]::Matches($body, '(?m)^\s*public [^\r\n(=]*?\s(\w+) \{ get;') | ForEach-Object { $_.Groups[1].Value }
        [regex]::Matches($body, '(?m)^\s+(\w+) = -?\d+,?\s*$') | ForEach-Object { $_.Groups[1].Value }
    )
    foreach ($name in @($types) + $members) { [void] $declared.Add($name) }

    # Ordinal, never a PowerShell hashtable: C# names are case-sensitive (accounts and Accounts are two types).
    $map = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($name in $declared) {
        $dotnet = ConvertTo-DotnetName $name
        if ($dotnet -cne $name) { $map[$name] = $dotnet }
    }
    # DECISION: a type whose .NET name another type already has keeps NSwag's name. The contract can
    # name one thing twice (an inline enum beside a schema of the same values); merging the two would
    # be a guess about the contract, and a suffix would be a name nobody wrote.
    $typeNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($type in $types) { if (-not $map.ContainsKey($type)) { [void] $typeNames.Add($type) } }
    foreach ($type in @($types | Where-Object { $map.ContainsKey($_) } | Sort-Object -CaseSensitive)) {
        if (-not $typeNames.Add($map[$type])) {
            Write-Warning "$type keeps its name: $($map[$type]) is another type"
            [void] $map.Remove($type)
            [void] $typeNames.Add($type)
        }
    }

    $tokens = '@"(?:[^"]|"")*"|"(?:\\.|[^"\\\n])*"|//[^\n]*|\b[A-Za-z_][A-Za-z0-9_]*\b'
    $renamed = [regex]::Replace($body, $tokens, [Text.RegularExpressions.MatchEvaluator] {
            param($token)
            $text = $token.Value
            if ($map.ContainsKey($text)) { return $map[$text] }
            return $text
        })
    # The enum's wire names, where System.Text.Json reads them.
    return [regex]::Replace($renamed,
        '(?m)^([ \t]*)\[System\.Runtime\.Serialization\.EnumMember\(Value = @"((?:[^"]|"")*)"\)\]',
        '$0' + "`n" + '$1[System.Text.Json.Serialization.JsonStringEnumMemberName(@"$2")]')
}

# A word the SDK was not generated with, from a newer API, reads as the enum's UnknownValue instead of
# failing the call. NJsonSchema has no setting for it, so after the renaming each enum the contract writes
# as words gains the member UnknownValue = -1, which no word names, and each enum property's converter is
# MfxHub.ContractEnumConverter (src/MfxHub.Sdk/Json.cs) in place of System.Text.Json's, which throws on an
# unlisted word; that converter reads one as UnknownValue and refuses UnknownValue in a request. An enum the
# contract writes as numbers is left alone: System.Text.Json reads any number into it already.
# DECISION: UnknownValue, not Unknown, and -1: see UnknownEnumValue in Json.cs. A contract word that would
# take the name is refused here, so the member can never shadow one.
function Add-UnknownValues([string] $body) {
    $body = $body.Replace(
        '[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]',
        '[System.Text.Json.Serialization.JsonConverter(typeof(global::MfxHub.ContractEnumConverter))]')
    return [regex]::Replace($body, '(?ms)^([ \t]*)public enum (\w+)(\r?\n[ \t]*\{\r?\n)(.*?^[ \t]*\})', [Text.RegularExpressions.MatchEvaluator] {
            param($found)
            $indent, $name, $open, $members = $found.Groups[1].Value, $found.Groups[2].Value, $found.Groups[3].Value, $found.Groups[4].Value
            if (-not $members.Contains('[System.Text.Json.Serialization.JsonStringEnumMemberName(')) { return $found.Value }
            if ($members -cmatch '(?m)^\s+UnknownValue = ') { throw "$name already has a member named UnknownValue" }
            $member = "`n$indent    /// <summary>A word this version of the SDK does not know, from a newer API. Read from an answer, never sent.</summary>`n$indent    UnknownValue = -1,`n"
            return "$($indent)public enum $name$open$member$members"
        })
}

# The keys of components.schemas, read as the suite's checker reads them: four spaces in, under "  schemas:".
function Get-SchemaKeys([string] $yaml) {
    $keys = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $inSchemas = $false
    foreach ($line in $yaml.Replace("`r`n", "`n").Split("`n")) {
        if ($line -ceq '  schemas:') { $inSchemas = $true; continue }
        if ($inSchemas -and $line -cmatch '^ {0,3}\S') { $inSchemas = $false }
        if ($inSchemas -and $line -cmatch '^ {4}([''"]?)([A-Za-z0-9_.-]+)\1:(?:\s|$)') { [void] $keys.Add($Matches[2]) }
    }
    return , $keys
}

# Each class or enum NSwag named after a schema key gains [ContractSchema("<key>")], so the key stays known
# whatever the type is called after the .NET renaming (docs/sdkref.json names it). Matched on NSwag's own name,
# before the renaming: NSwag gives a schema its key as the class name, and a type it makes for an inline schema
# that would take the same name gets a number instead (Account is the schema, Account2 an inline object). A
# schema with no class (a string, a oneOf) has nothing to mark.
function Add-ContractSchemas([string] $body, $keys) {
    return [regex]::Replace($body, '(?m)^([ \t]*)(public (?:partial class|enum) (\w+))', [Text.RegularExpressions.MatchEvaluator] {
            param($found)
            $name = $found.Groups[3].Value
            if (-not $keys.Contains($name)) { return $found.Value }
            return $found.Groups[1].Value + '[global::MfxHub.ContractSchema("' + $name + '")]' + "`n" + $found.Value
        })
}

Push-Location $root
try {
    dotnet tool restore | Out-Null
    $out = 'src/MfxHub.Sdk/Generated/Models.g.cs'
    dotnet nswag openapi2csclient `
        /input:spec/openapi.yaml `
        /output:$out `
        /namespace:MfxHub.Models `
        /GenerateClientClasses:false `
        /GenerateExceptionClasses:false `
        /GenerateDtoTypes:true `
        /JsonLibrary:SystemTextJson `
        /GenerateNullableReferenceTypes:true `
        /GenerateOptionalPropertiesAsNullable:true `
        /GenerateDataAnnotations:false `
        /TypeAccessModifier:public | Out-Null
    $keys = Get-SchemaKeys (Get-Content -Raw spec/openapi.yaml)
    $body = Add-UnknownValues (ConvertTo-DotnetStyle (Add-ContractSchemas (Get-Content -Raw $out) $keys))
    $header = "// <auto-generated> by tools/generate.ps1 from spec/openapi.yaml; do not edit. </auto-generated>`n#nullable enable`n#pragma warning disable`n"
    # NSwag stamps its own version and the time; drop the time so the output is the same every run.
    $body = [regex]::Replace($body, '(?m)^//\s+Generated using the NSwag toolchain.*$', '// Generated using the NSwag toolchain (see .config/dotnet-tools.json)')
    [IO.File]::WriteAllText((Join-Path $root $out), ($header + $body).Replace("`r`n", "`n"))
    Write-Output "wrote $out"
}
finally {
    Pop-Location
}
