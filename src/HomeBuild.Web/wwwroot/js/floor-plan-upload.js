// <floor-plan-upload>: uploads a PDF straight from the browser to S3 with the API's pre-signed POST, then asks the
// API (through this site) to check the file and move it into place. The size and type checks here are only quick
// feedback: S3 enforces the POST policy and the API checks the stored object.
class FloorPlanUpload extends HTMLElement {
  connectedCallback() {
    this.querySelector("button").addEventListener("click", () => this.upload());
  }

  status(text) {
    this.querySelector("[role=status]").textContent = text;
  }

  async post(url, body) {
    const token = this.querySelector("input[name=__RequestVerificationToken]").value;
    const response = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json", RequestVerificationToken: token },
      body: JSON.stringify(body ?? {}),
    });
    return response;
  }

  async upload() {
    const file = this.querySelector("input[type=file]").files[0];
    if (!file) {
      this.status("Choose a PDF first.");
      return;
    }

    this.status("Getting an upload link…");
    const ticketResponse = await this.post(this.dataset.ticket);
    if (!ticketResponse.ok) {
      this.status("Could not start the upload.");
      return;
    }

    const ticket = await ticketResponse.json();
    if (file.size > ticket.maxBytes || file.type !== ticket.contentType) {
      this.status(`Choose a PDF of at most ${ticket.maxBytes / 1024 / 1024} MB.`);
      return;
    }

    this.status("Uploading…");
    const form = new FormData();
    for (const [name, value] of Object.entries(ticket.fields)) {
      form.append(name, value);
    }
    form.append("file", file); // the file must be the last field
    const s3 = await fetch(ticket.url, { method: "POST", body: form });
    if (!s3.ok) {
      this.status(`Storage refused the file (${s3.status}).`);
      return;
    }

    this.status("Checking the file…");
    const complete = await this.post(this.dataset.complete, { key: ticket.key });
    if (complete.ok) {
      this.status("Floor plan saved.");
    } else {
      const problem = await complete.json().catch(() => ({}));
      this.status(problem.detail ?? problem.title ?? "The file was not accepted.");
    }
  }
}

customElements.define("floor-plan-upload", FloorPlanUpload);
