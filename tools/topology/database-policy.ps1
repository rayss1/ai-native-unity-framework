$ErrorActionPreference='Stop'
# Never serialize the builder, its connection string, or parser exceptions.
try {
 $connection=$env:AINATIVE_TEST_POSTGRES
 if([string]::IsNullOrWhiteSpace($connection)){throw 'Missing database configuration'}
 $builder=[System.Data.Common.DbConnectionStringBuilder]::new()
 $builder.set_ConnectionString($connection)
 # DbConnectionStringBuilder discards unquoted empty assignments. Reject an
 # explicit empty Pooling instead of silently reporting it as an omitted default.
 # Consume quoted values whole so a password containing ';Pooling=' is inert.
 $assignments=[regex]::Matches($connection,'(?:^|;)\s*(?<key>[^=;]+?)\s*=\s*(?<value>"(?:[^"]|"")*"|''(?:[^'']|'''')*''|[^;]*)(?=;|$)')
 foreach($assignment in $assignments) {
  if($assignment.Groups['key'].Value.Trim() -ieq 'Pooling' -and
     [string]::IsNullOrWhiteSpace($assignment.Groups['value'].Value)){throw 'Empty pooling value'}
 }
 $explicit=$builder.ContainsKey('Pooling')
 # Npgsql 10.0.3 defaults to pooling enabled when the option is absent.
 $enabled=$true
 if($explicit) {
  $value=([string]$builder['Pooling']).Trim()
  if($value -ieq 'true'){$enabled=$true}
  elseif($value -ieq 'false'){$enabled=$false}
  else{throw 'Invalid pooling value'}
 }
} catch {
 throw 'Invalid AINATIVE_TEST_POSTGRES database policy; connection details withheld'
}
[pscustomobject]@{Provider='Npgsql';PoolingEnabled=$enabled;PoolingExplicitlyConfigured=$explicit}
