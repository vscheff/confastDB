window.confastCriteriaReorder = (() => {
    const instances = new WeakMap();

    function initialize(table, dotNetReference) {
        const existing = instances.get(table);
        existing?.dispose();

        const onPointerDown = event => {
            if (event.button !== 0 || table.dataset.reordering === "true") {
                return;
            }

            const handle = event.target.closest(".criterion-drag-handle");
            const sourceRow = handle?.closest("tr[data-criterion-id]");
            if (!sourceRow || !table.contains(sourceRow)) {
                return;
            }

            event.preventDefault();
            handle.setPointerCapture?.(event.pointerId);
            const sourceId = Number(sourceRow.dataset.criterionId);
            let targetRow;

            sourceRow.classList.add("criterion-dragging");
            table.classList.add("criteria-reordering");

            const setTarget = row => {
                if (targetRow === row) {
                    return;
                }

                targetRow?.classList.remove("criterion-drop-target");
                targetRow = row;
                targetRow?.classList.add("criterion-drop-target");
            };

            const onPointerMove = moveEvent => {
                const row = document.elementFromPoint(moveEvent.clientX, moveEvent.clientY)
                    ?.closest("tr[data-criterion-id]");
                setTarget(row && row !== sourceRow && table.contains(row) ? row : undefined);
            };

            const finish = async () => {
                document.removeEventListener("pointermove", onPointerMove);
                document.removeEventListener("pointerup", onPointerUp);
                document.removeEventListener("pointercancel", onPointerCancel);
                sourceRow.classList.remove("criterion-dragging");
                targetRow?.classList.remove("criterion-drop-target");
                table.classList.remove("criteria-reordering");

                if (!targetRow) {
                    return;
                }

                table.dataset.reordering = "true";
                try {
                    await dotNetReference.invokeMethodAsync("DropCriterionAsync", sourceId, Number(targetRow.dataset.criterionId));
                } finally {
                    delete table.dataset.reordering;
                }
            };

            const onPointerUp = () => void finish();
            const onPointerCancel = () => {
                targetRow = undefined;
                void finish();
            };

            document.addEventListener("pointermove", onPointerMove);
            document.addEventListener("pointerup", onPointerUp);
            document.addEventListener("pointercancel", onPointerCancel);
        };

        table.addEventListener("pointerdown", onPointerDown);
        instances.set(table, { dispose: () => table.removeEventListener("pointerdown", onPointerDown) });
    }

    return { initialize };
})();
