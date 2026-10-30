# Security

## Reporting a vulnerability

If you find a security problem in Tidy, please **don't open a public issue**. Report it privately through GitHub: go to the [Security tab](https://github.com/Jeray665/Tidy/security) and press **Report a vulnerability**.

Include what you found, how to reproduce it and which Tidy version you used. You'll get an answer as soon as possible, and the fix will go out in a new version with credit to you if you want it.

## Supported versions

Only the [latest version](https://github.com/Jeray665/Tidy/releases/latest) gets security fixes. Tidy updates itself from Settings.

## Checking your download

Every release publishes the SHA-256 fingerprint of `Tidy-Setup.exe`. Compare it with yours before running it:

```
certutil -hashfile Tidy-Setup.exe SHA256
```

---

**Español:** si encuentras un fallo de seguridad, no abras una incidencia pública. Avísalo en privado desde la [pestaña Security](https://github.com/Jeray665/Tidy/security) con **Report a vulnerability**. Solo la última versión recibe arreglos de seguridad.
