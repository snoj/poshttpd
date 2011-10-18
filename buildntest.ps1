$invokation = $MyInvocation
$basePath = Split-Path -Parent $invokation.InvocationName


clear;
try {

    C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe ("/out:{0}\poshttpd.exe" -f $basePath) /target:exe ("{0}\main.cs" -f $basePath) `
    ("/r:{0}" -f (ls "C:\Windows\assembly\GAC_MSIL\*\*\System.Management.Automation.dll" |select -First 1).fullname) 2>&1 | tee -Variable tmp;
    
    $tmp | Select-String error | %{ throw "errors!"; }

    & ("{0}\poshttpd.exe" -f $basePath) .\poshttpd.config -t eth0 ;
} catch {
    
}