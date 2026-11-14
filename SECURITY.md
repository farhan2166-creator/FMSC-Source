# Security

Do not include credentials, school databases, backups, student/staff records or
production screenshots in source, issues, pull requests, logs or Actions artifacts.
Use fictional fixtures and keep the detached release database pair private.

Report vulnerabilities using GitHub private vulnerability reporting when enabled.
Do not disclose credentials or personal records in a public issue.

The FMSC Security Checks workflow scans available Git history, working files and
unpacked historical DACPAC metadata using Gitleaks. It rejects common sensitive
file names and database packages with exported data. A clean scan does not establish
that screenshots, SQL literals or documents contain no personal information.

Before changing repository visibility, review tracked files, Git history, workflow artifacts and configuration for school data and credentials. Keep private distribution repositories private.
