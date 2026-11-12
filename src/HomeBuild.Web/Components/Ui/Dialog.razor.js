// A native modal: the browser makes the rest of the page inert (focus cannot leave) and close() returns focus
// to the control that opened it. Escape raises "cancel", which does not bubble, so Blazor's delegated @oncancel
// never sees it: handle it here, keep the dialog open, and let .NET decide (the parent's Open parameter).
export function show(dialog, component) {
  if (!dialog.dataset.cancelHandled) {
    dialog.dataset.cancelHandled = "true";
    dialog.addEventListener("cancel", event => {
      event.preventDefault();
      component.invokeMethodAsync("CloseFromBrowser");
    });
  }
  if (!dialog.open) {
    dialog.showModal();
  }
}

export function close(dialog) {
  if (dialog.open) {
    dialog.close();
  }
}
