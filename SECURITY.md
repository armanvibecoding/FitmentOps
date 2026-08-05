# Security Policy

## Supported code

Security fixes target the latest commit on the default branch. Older commits and local forks are not maintained releases.

## Reporting a vulnerability

Use [GitHub private vulnerability reporting](https://github.com/armanvibecoding/FitmentOps/security/advisories/new). Do not open a public issue for a suspected vulnerability.

Include only the minimum information needed to reproduce and assess the problem:

- affected commit and component;
- prerequisites and reproduction steps;
- expected and observed behavior;
- realistic impact;
- a minimal proof of concept with secrets and personal data removed.

Do not test against systems or accounts you do not own. Do not access, retain, or disclose other users' data. Reports that require credential theft, CAPTCHA bypass, social engineering, destructive load, or third-party disruption are outside the permitted testing scope.

## Secret handling

Never commit passwords, API keys, signing secrets, provider tokens, cookies, personal exports, or production logs. Use environment variables or the deployment platform's managed secret store. If a secret is exposed, revoke and rotate it before removing it from Git history.

## Security-sensitive changes

Changes to authentication, authorization, checkout, payments, refunds, webhooks, inventory, migrations, personal data, audit records, or provider adapters require:

1. a regression test covering the security invariant;
2. explicit failure-path behavior;
3. dependency and secret scans;
4. rollout and rollback notes;
5. review of logs, metrics, and retained data.
