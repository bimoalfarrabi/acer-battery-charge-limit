@{
    Severity = @('Error', 'Warning')
    ExcludeRules = @(
        # Get-WmiObject is required for the verified firmware-provider call and exists only in the Windows PowerShell 5.1-pinned worker, not PowerShell 7.
        'PSAvoidUsingWMICmdlet'
        # The tool uses these small helper names: Say, Emit, and Describe.
        'PSUseApprovedVerbs'
        # Installer progress messages are intentionally interactive.
        'PSAvoidUsingWriteHost'
        # Keep existing noun names as written; do not require singular nouns.
        'PSUseSingularNouns'
    )
}
