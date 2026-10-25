# Hoja de ruta de Tidy

Se trabaja por fases, de arriba abajo. La primera versión pública (1.0.0) sale cuando estén las tres fases. Cada punto se marca al terminarlo y probarlo (pruebas automáticas en carpeta aislada + revisión visual).

## Fase 1 · Arreglos y base (hecha)

- [x] **BUG: "Analizar el disco C:" (CHKDSK)** abre la consola sin permisos de administrador.
- [x] **BUG: Hardware muestra mal las pantallas.** Con 3 monitores solo aparece uno (1080p a 144 Hz). Debe listar todas, con resolución y frecuencia reales.
- [x] **Carpetas y registros en inglés** (`logs`, `backups`, `quarantine`…), por compatibilidad. Se migran solos los datos que ya existan, sin perder copias de seguridad.
- [x] **Iconos claro y oscuro:** el claro por defecto; el oscuro cuando la app está en modo oscuro (ventana, barra de tareas y bandeja).
- [x] **Elegir tema en Ajustes:** Sistema, Claro u Oscuro.
- [x] **Instalador y versión portátil** con el mismo `Tidy.exe`:
  - Instalar: Archivos de programa, menú Inicio y "Aplicaciones instaladas".
  - Portátil: todo se guarda junto al .exe y no deja nada en el PC.
- [x] **Actualizaciones seguras desde GitHub Releases:**
  - Aviso **"Nueva actualización"** en el menú lateral.
  - Botón **Actualizar** que comprueba el archivo descargado (tamaño, huella SHA-256, producto y versión) y conserva configuración, copias de seguridad e historial.
  - Al abrir la versión nueva, una ventana de **novedades** encima de la pantalla principal, con su botón de cerrar.
  - En Ajustes: versión actual, "Buscar actualizaciones" y enlace a la página de GitHub.
  - Si una versión futura quita una función que habías usado, sus cambios se podrán **restablecer desde Ajustes** (sus copias de seguridad se conservan).

- [x] Icono antiguo en el menú Inicio tras actualizar: Windows guardaba en caché el de la primera versión; ahora se le avisa al instalar o actualizar.

## Fase 2 · Experiencia (en curso)

- [x] **Notificaciones nuevas:** más bonitas, varias a la vez (máximo 3 visibles, sin listas infinitas). Si se repite la misma, se agrupa con un contador "×3" en vez de duplicarse.
- [x] **Plan de energía configurable:** decidir si el plan aplicado suspende el PC, apaga la pantalla o hiberna (y a los cuántos minutos), o nunca.
- [x] **Mini app de la bandeja más completa:** secciones a la izquierda (Resumen, Rendimiento, Limpieza) y controles rápidos (RAM, plan de energía, modo juego, limpieza con lista previa) sin entrar en la app grande. El clic derecho tiene submenú de plan de energía y modo juego.
- [x] **Varios idiomas** elegibles en Ajustes: español e inglés (o el de Windows). `tools/i18n.py` recoge todos los textos y la publicación se para si falta alguna traducción.

## Fase 3 · Más control y calidad

- [x] **Lo que arranca solo y solo quita rendimiento:** procesos, servicios, tareas y apps en segundo plano de terceros, con preconfiguraciones por nivel de rendimiento. Siempre con lista previa, explicación y deshacer.
- [x] **Optimizar la propia app:** medido: abre la ventana en menos de 1 s; en la bandeja usa unos 118 MB y casi nada de CPU (0,05 s por minuto). Al cerrar la ventana devuelve su memoria, y las lecturas en vivo solo corren con algo visible.
- [x] **Revisión de bugs archivo por archivo**, sin ejecutar nada en el PC: arreglados el deshacer de valores del registro de cualquier tipo, la copia previa obligatoria al desinstalar servicios y tareas, el plan de energía que quedaba en "Máximo" si cerrabas Tidy jugando, descargas a medias que no se borraban y varios errores que dejaban pantallas a medio cargar.

## Hecho

- Limpieza, almacenamiento, optimizaciones con perfiles (con casillas para elegir), desinstalador en profundidad, procesos, servicios y tareas, red, herramientas, copias de seguridad.
- Seguridad (revisión de solo lectura), salud de discos, apps con winget, modo juego automático, antes y después, recordatorio de limpieza, historial con deshacer.
- Licencia GPL-3.0.
