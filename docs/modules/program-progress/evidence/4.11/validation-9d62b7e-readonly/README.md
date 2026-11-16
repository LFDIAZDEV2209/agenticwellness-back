# Revisión back de 4.11 contra front 9d62b7e

Runtime API 5122: e0a9c29. Los ocho archivos de la entrega e2e coinciden con el
commit indicado (front-files.json); no se afirma limpieza de todo el worktree front.

Playwright terminó exit 0: **2 passed, 3 skipped**. El caso con POST submit replay
se excluyó expresamente mediante --grep-invert replay para respetar la instrucción
vigente de no cambiar datos. En su lugar, validate-post-tests-readonly.py ejecutó
**4 GET / 13 aserciones**: 9/9 completados, inscripción/propuesta únicas, 12 semanas,
plan publicado de 30 días y estado histórico PendingAssignment semana 0.
No se acredita en esta revisión una nueva transición 8→9.

La suite ejecutó otros **7 GET de producto**, todos 200. La sugerencia draft conservó
ID, cinco alternativas sintéticas y ninguna aplicación; las dos lecturas de semana
conservaron cuerpo/hash. Las seis familias de tablas y sentinelas de before/after
coinciden; no hubo POST de negocio, generación, apply ni publicación.
Los logins QA sí crean sesiones/tokens Auth: “cero escrituras” se limita a contenido,
plan, programa, evaluaciones, XP y cierres; no se afirma inmovilidad global de Auth.

IA caída: los handlers/clientes/guard/stores EF reales se ejecutaron con PostgreSQL
READ ONLY contra dos respuestas HTTP 503 de Postman, obteniendo summary/weekly
unavailable sin guardar borradores ni aplicar. Después, los GET reales de plan,
snapshot y borrador siguieron 200. No se detuvo la IA compartida. Esto acredita la
capa back; no inyecta un 503 a través de controllers del host 5122 ni acredita UI.
Los tres escenarios de pantalla continúan fixme por el rediseño: **4.11 UI pendiente**.

Se usó el plugin Postman (api-testing/api-simulation) para las dependencias 503.
Solo se cerraron los procesos de simulación propios; processes-closed.json registra
el cierre. El primer wrapper se interrumpió por warning de colores de Node aunque
el runner llegó a pasar; se conservó bajo first-run-wrapper-warning y se repitió
obteniendo exit 0 real. No se reinició ningún servicio compartido.

Esta evidencia precede a la reparación D de 1.14 y es histórica de e0a9c29. El
arranque A2/F2 corregido se acredita aparte en PostgreSQL aislado; no se reescriben
la fixture, sus estados históricos o estos resultados para aparentar actualización.
