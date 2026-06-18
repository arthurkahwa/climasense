# Deploying ClimaSense.Monitor to IIS

## Prerequisites (Windows server)
- Install the **ASP.NET Core 10 Hosting Bundle** (ANCM + runtime), then `iisreset`.

## Publish (from the Mac)
```
dotnet publish src/ClimaSense.Monitor -c Release -o ./publish
```
Copy `./publish` to the server, e.g. `C:\inetpub\climasense`.

## IIS
1. Create an app pool with **.NET CLR version = No Managed Code**.
2. Create a site/app pointing at the publish folder, using that pool; add an HTTPS binding.
3. Set the connection string (machine or site env var):
   ```
   setx CLIMASENSE_UPS3_CONNECTION "Server=util02.lab.local,1433;Database=ups3;User ID=<your-db-user>;Password=<your-db-password>;Encrypt=True;TrustServerCertificate=True" /M
   ```
   (or add it under `<aspNetCore><environmentVariables>` in `web.config`, and consider encrypting it).
4. Browse the site; check `/health` returns 200.

## Optional hardening
- Run `scripts/ups3-index.sql` (query performance — nonclustered index on `sensor_dateTime`).
- Run `scripts/climasense_ro.sql` and switch the connection string to `climasense_ro` to drop the `sysadmin` `<your-db-user>` usage to read-only `SELECT`.
