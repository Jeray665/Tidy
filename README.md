# Tidy

Optimizador seguro para Windows 11: limpieza, optimizaciones, drivers, apps, red, seguridad y modo juego, en español.

![Tidy: tu PC, en plena forma. Sin romper nada.](docs/img/hero.jpg)

**Descarga:** la última versión está en [Releases](../../releases/latest).

- **Tidy-Setup.exe**: lo instala en Archivos de programa, con acceso en el menú Inicio y desinstalación desde Configuración > Aplicaciones. Se actualiza solo desde la app (con comprobación de huella SHA-256).
- **Tidy-Portable.exe**: sin instalar. Guarda todo en una carpeta `TidyData` junto al .exe y no deja nada más en el PC.

## Qué hace

- **Limpieza y almacenamiento:** temporales, cachés, archivos grandes, descargas y duplicados. Antes de borrar te enseña la lista exacta de archivos; lo de Almacenamiento va a la Papelera.
- **Optimizaciones:** más de 80, con perfiles (Recomendado, Gaming, Privacidad, Rendimiento máximo). Cada una guarda el valor original y se deshace con su interruptor.
- **Aplicaciones:** desinstalador en profundidad (los restos van a una cuarentena recuperable), procesos, y actualizar o instalar apps con winget.
- **Rendimiento:** liberación automática de RAM, planes de energía y modo juego automático.
- **Drivers:** desde Windows Update y nvidia.com (se comprueba la firma digital).
- **Red, servicios, tareas e inicio de Windows**, con copia de seguridad de todo.
- **Seguridad:** revisión de solo lectura (antivirus, firewall, UAC, Secure Boot, TPM, discos…).
- **Historial:** todo lo que hace Tidy, con botón para deshacer, y comparación antes/después.

![Limpieza, optimizaciones y rendimiento](docs/img/funciones.jpg)

![Las secciones de Tidy: servicios, almacenamiento, inicio de Windows y hardware](docs/img/secciones.jpg)

![Tema claro y oscuro](docs/img/temas.jpg)

## Lo que Tidy no hace a propósito

No desactiva Defender, el firewall, el UAC, SmartScreen, Windows Update ni las protecciones contra Spectre/Meltdown. Ninguna optimización baja la seguridad del PC.

![Optimiza sin miedo: todo se deshace](docs/img/seguro.jpg)

## Requisitos

Windows 11 (64 bits) y el [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

## Compilar

```
dotnet publish src/Tidy -c Release -p:PublishSingleFile=true -p:DebugType=none -o dist
```

Pruebas de seguridad (se ejecutan en una carpeta aislada):

```
dotnet run --project tests/Tidy.Tests
```

Publicar una versión (sube `<Version>` en `src/Tidy/Tidy.csproj`, añade su entrada a `CHANGELOG.md` y haz commit antes):

```
python tools/release.py 1.2.0
```

Compila, crea Tidy-Setup.exe, Tidy-Portable.exe y su `.sha256`, publica la release y comprueba en GitHub que las notas no tienen acentos rotos y que la huella coincide.

## Licencia

Tidy es software libre bajo la [GNU GPL v3](LICENSE): puedes usarlo, estudiarlo, modificarlo y compartirlo, y cualquier versión modificada que se publique tiene que seguir siendo libre y con su código.
