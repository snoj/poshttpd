param(
	$Context
)

#Change the content type.
$Context.Response.ContentType = "text/plain";

$Context.Response.ContentType = "text/html"; #now html again!
@"
<!doctype html>
<html>
	<head>
		<title>test page</title>
	</head>
	<body>
		<div>$(Get-Process | Select -First 10 Product, Name, Company, Description, Path, ID | sort ProcessName | ConvertTo-Html -Frag)</div>
"@
#only strings are shown.
1234;

"-"*100 + "<br />";

1234 | out-string; #see....

@"
	</body>
</html>
"@;