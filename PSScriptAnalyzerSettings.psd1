# PSScriptAnalyzer settings for this repository's PowerShell scripts (setup.ps1, set-local-secrets.ps1,
# infra/entra-setup.ps1). Used by CI and locally:
#   Invoke-ScriptAnalyzer -Path <script> -Settings ./PSScriptAnalyzerSettings.psd1
# Individual exceptions (e.g. a plain-text password that sqlcmd needs) are suppressed in the script itself,
# with a justification, rather than turned off here for every script.
@{
    Severity     = @('Error', 'Warning')
    ExcludeRules = @(
        # These scripts are run by a person at a terminal and print coloured progress. Write-Host is the
        # right tool for that: Write-Output would mix the messages into the pipeline output.
        'PSAvoidUsingWriteHost'
    )
}
