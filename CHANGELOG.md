# Novedades de Tidy

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
