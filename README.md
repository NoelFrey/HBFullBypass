# HBFullBypass

This project was inspired by [Sh3lldon’s FullBypass](https://github.com/Sh3lldon/FullBypass/tree/main) and [S3cur3Th1sSh1t’s blog post](https://www.r-tec.net/r-tec-blog-bypass-amsi-in-2025.html) published in February 2025.

**HBFullBypass** is a tool that:

- Bypasses AMSI (AntiMalware Scan Interface) using **hardware breakpoints**.
- Delivers a **reverse PowerShell shell** in Full Language Mode using little obfuscated **Nishang’s reverse shell one-liner**.

```Text
P.S. Please do not use in unethical hacking and follow all rules and regulations of laws
```

## Usage

**Deliver the Project File**:
Deliver the HBypass.csproj file onto the target machine, place it in a writable directory such as `C:\Windows\Tasks` or `C:\Windows\Temp`.
**Execute with MSBuild**:
Run the project using msbuild.exe from the .NET Framework directory. Example command:

```bash
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\msbuild.exe .\HBFullBypass.csproj
```

Tool performs two main actions:

### 1. AMSI Bypass

- Utilizes hardware breakpoints to intercept the AmsiScanBuffer function in amsi.dll.
- Registers a Vectored Exception Handler to catch single-step exceptions triggered by the breakpoint.
- Modifies the AMSI scan result to always return AMSI_RESULT_CLEAN (0), effectively bypassing AMSI detection for subsequent PowerShell commands and scripts.

### 2. PowerShell Reverse Shell

- Prompts for an attacker's IP address and port for webserver.
- Downloads and executes a PowerShell reverse shell script (rev.ps1).
- Grants a Full Language Mode PowerShell reverse shell, unrestricted by Constrained Language Mode (CLM). `$ExecutionContext.SessionState.LanguageMode`.
