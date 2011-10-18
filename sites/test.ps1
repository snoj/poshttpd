param(
	$Context
)

#Change the content type.
$Context.Response.ContentType = "text/plain";

$Context.Response.ContentType = "text/html"; #now html again!

#Get the process list and convert to some fancy html tables!
Get-Process | Select -First 10 Product, Name, Company, Description, Path, ID | sort ProcessName | ConvertTo-Html;

#only strings are shown.
1234;

"-"*100 + "<br />";

1234 | out-string; #see....