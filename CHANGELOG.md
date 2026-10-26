# Novedades de Tidy

## 1.2.0
- Imágenes: convierte entre JPG, PNG, BMP, TIFF, GIF e ICO (también abre HEIC, WebP y AVIF), comprime, reduce el tamaño, quita datos ocultos como el GPS y endereza. Los originales no se tocan: cada imagen se guarda como copia nueva.
- Vídeo y audio: comprime vídeos a MP4 o saca solo el sonido en MP3 o M4A, con el motor de Windows y prioridad baja para que puedas seguir usando el PC. Los originales no se tocan.
- Comprobar un archivo: compara su huella (SHA-256, SHA-512, SHA-1 o MD5) con la que publica la web y te dice si el programa lleva una firma digital válida.
- Nuevos idiomas: portugués, francés, alemán, ruso, chino y japonés, a elegir en Ajustes.
- El aviso al aplicar o deshacer optimizaciones y el de reiniciar tras una reparación salían en español con Tidy en otro idioma.

## 1.1.3
- Apps: el catálogo para instalar muestra siempre la versión más nueva de Python que hay (hoy la 3.14), también cuando salgan versiones nuevas, sin esperar a que se actualice Tidy.

## 1.1.2
- Apps: si un programa no se deja instalar ni actualizar como administrador (por ejemplo Spicetify), Tidy lo vuelve a intentar solo, como tu usuario normal y sin permisos de administrador.
- Cerrar una notificación con la X ya no muestra el error "Algo ha fallado".

## 1.1.1
- Descargas y Duplicados tienen ahora los mismos filtros que el análisis de disco: por tipo de archivo, por tamaño y columnas que se ordenan con un clic. En Duplicados cada grupo de copias se mueve entero.
- Descargas: "Mover a otra carpeta", igual que en el análisis de disco, y se devuelve todo a su sitio desde Historial.
- La pestaña Descargas ya no deja la app sin responder mientras carga muchos archivos.
- El archivo en el que haces clic se marca en gris en vez de con el color de Windows.
- El aviso de versión nueva aparece justo después de publicarse, sin esperar.
- Textos que salían en español con Tidy en inglés.

## 1.1.0
- Al abrir Tidy se comprueba si hay versión nueva, y si la hay sale un aviso arriba con "Actualizar" y "Ocultar aviso".
- Análisis de disco renovado: tabla con filtros por tipo (documentos, imágenes, comprimidos, vídeos, audio) y por tamaño, columnas que se ordenan con un clic y el total de lo que marcas.
- "Mover a otra carpeta" en el análisis de disco, por ejemplo a otro disco para liberar C:. No pisa ningún archivo y se devuelve todo a su sitio desde Historial.
- Más seguro: los archivos de programas (AppData) y de juegos instalados (Steam, Xbox, Epic, Riot, EA…) ya no se pueden marcar para mover ni borrar.
- Las listas largas (aplicaciones, procesos, servicios, tareas, almacenamiento) ocupan toda la ventana en vez de quedarse cortas.
- Las pestañas de cada sección marcan claramente en cuál estás, en modo claro y oscuro.

## 1.0.4
- Menú nuevo, más corto: las secciones parecidas van juntas en pestañas (por ejemplo, Rendimiento, Inicio de Windows y Optimizaciones), y lo técnico queda en Avanzado.
- Copias del archivo hosts: solo se guarda una copia cuando el archivo cambia de verdad. "Qué cambia" te enseña todo lo que se deshace al restaurar una copia, paso a paso, y "Borrar copias" te deja quitar las repetidas eligiendo tú cuáles.
- Inicio de Windows: "¿Qué alarga el arranque?" te enseña lo que Windows ha visto retrasar tus arranques del último mes y cuántos segundos, y te deja quitar del inicio las apps que se pueden quitar.
- Ajustes: limpieza programada. Elige si Tidy te avisa o limpia solo, cada cuánto y qué categorías. La papelera nunca se vacía sola, y cada limpieza automática queda en Historial con la lista de lo borrado.

## 1.0.3
- Procesos: "¿Qué gasta en segundo plano?" mide durante 10 segundos los programas abiertos sin ventana y te enseña lo que gasta cada uno. Puedes quitarlos del inicio o cerrarlos, siempre marcando tú cada uno; nunca salen antivirus, drivers, audio, VPN ni lo que controla tu hardware.

## 1.0.2
- Analizar el disco C: muestra mensajes claros (qué hace y el resultado) en vez del texto de Windows con los acentos rotos.

## 1.0.1
- Servicios y tareas: Tidy detecta los de Windows y de tus programas que están activos sin hacer falta (telemetría, informes de errores, funciones que casi nadie usa) y te enseña la lista con el porqué de cada uno antes de cambiar nada.
- Analizar el disco C: funciona aunque algo en el PC impida abrir chkdsk.exe: hace el mismo análisis de solo lectura con el servicio de almacenamiento de Windows.
- Las listas de preconfiguraciones salen enteras en inglés si usas Tidy en inglés.

## 1.0.0
- Primera versión: limpieza, almacenamiento, optimizaciones con perfiles, desinstalador en profundidad, procesos, apps con winget, servicios y tareas, red, drivers, revisión de seguridad, modo juego automático e historial con deshacer.
- Instalador (Tidy-Setup.exe) con acceso en el menú Inicio y en "Aplicaciones instaladas", y versión portátil (Tidy-Portable.exe) que guarda todo junto al .exe.
- Actualizaciones desde GitHub: aviso en el menú y botón para actualizar, comprobando tamaño y huella SHA-256 y conservando tus ajustes, copias e historial.
- Tema claro, oscuro o el de Windows, con icono que cambia con el tema.
- En español o en inglés (o el idioma de Windows), a elegir en Ajustes.
- Mini app en la bandeja con resumen, rendimiento y limpieza, y menú con plan de energía y modo juego.
- Preconfiguraciones de inicio por nivel para quitar lo que arranca solo, siempre con la lista antes y deshacer en Historial.
- Plan de energía a tu gusto: cuándo se apaga la pantalla, se suspende o hiberna el PC.
