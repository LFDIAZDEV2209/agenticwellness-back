/** ERP + API + Auth + PostgreSQL reales. El correo se captura en memoria. */
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { randomUUID } from "node:crypto";
import assert from "node:assert/strict";

const erpDir = process.env.COP_E2E_ERP_DIR;
const fixturePath = process.env.COP_WELCOME_FIXTURE;
assert.ok(erpDir && fixturePath?.startsWith("/tmp/"), "ERP directory and private QA fixture required");
const fixture = JSON.parse(readFileSync(fixturePath, "utf8"));
assert.match(fixture.database, /^copp_review_medications_welcome_/);
assert.equal(fixture.gateway, "http://127.0.0.1:5160");
const erp = "http://127.0.0.1:3164";
const { chromium, request } = await import(pathToFileURL(resolve(erpDir, "node_modules/playwright/index.mjs")));
const { expect } = await import(pathToFileURL(resolve(erpDir, "node_modules/@playwright/test/index.mjs")));
const browser = await chromium.launch();
const http = await request.newContext({ baseURL: fixture.gateway });
const suffix = randomUUID().slice(0, 8);
const patient = {
    firstName: "Ana",
    lastName: "Bienvenida QA",
    documentNumber: "QA-W-" + suffix,
    email: "welcome-" + suffix + "@example.invalid",
};

async function inbox() {
    const response = await http.get("/__qa/welcome-inbox");
    assert.equal(response.status(), 200);
    return response.json();
}

function welcome(messages, email) {
    return messages.filter(message => message.to === email && message.subject === "Bienvenido a Copp Adresd");
}

async function waitForWelcome(email) {
    await expect.poll(async () => welcome(await inbox(), email).length, {
        timeout: 36_000,
        intervals: [500, 1000],
        message: "Welcome email delivered after ERP creation and before first sign-in",
    }).toBe(1);
    return welcome(await inbox(), email)[0];
}

try {
    const context = await browser.newContext({ locale: "es-CO" });
    const page = await context.newPage();
    await page.goto(erp + "/login");
    await page.locator("#email").fill(fixture.accounts.manager.login);
    await page.locator("#password").fill(fixture.accounts.manager.password);
    const loggedIn = page.waitForResponse(response =>
        new URL(response.url()).pathname === "/api/auth/login" && response.request().method() === "POST");
    await page.getByRole("slider", { name: "Comencemos", exact: true }).press("End");
    const login = await loggedIn;
    assert.equal(login.status(), 200);
    const headers = { Authorization: "Bearer " + (await login.json()).accessToken };
    await page.waitForURL("**/dashboard", { timeout: 60_000 });
    await page.goto(erp + "/patients/new");
    await page.getByPlaceholder("Ej. María", { exact: true }).fill(patient.firstName);
    await page.getByPlaceholder("Ej. González", { exact: true }).fill(patient.lastName);
    await page.getByPlaceholder("1234567890", { exact: true }).fill(patient.documentNumber);
    await page.locator("input[type=date]").first().fill("1990-05-20");
    await page.getByRole("combobox", { name: "Género", exact: true }).click();
    await page.getByRole("option", { name: "Femenino", exact: true }).click();
    await page.getByPlaceholder("300 000 0000", { exact: true }).first().fill("3005550101");
    await page.getByPlaceholder("ej. usuario@coppaddresd.com", { exact: true }).first().fill(patient.email);
    await page.getByRole("button", { name: "Continuar", exact: true }).click();
    const submit = page.getByRole("button", { name: "Crear paciente", exact: true });
    if (!await submit.isVisible()) await page.getByRole("button", { name: "Continuar", exact: true }).click();
    const posted = page.waitForResponse(response =>
        new URL(response.url()).pathname === "/api/v1/patients" && response.request().method() === "POST");
    await page.getByRole("button", { name: "Crear paciente", exact: true }).click();
    const createdResponse = await posted;
    assert.equal(createdResponse.status(), 201, await createdResponse.text());
    const { id: patientId } = await createdResponse.json();
    await expect(page.getByRole("heading", { name: "Paciente creado correctamente" })).toBeVisible();
    console.log("ERP patient created successfully");
    const message = await waitForWelcome(patient.email);
    assert.match(message.plainTextBody, /App Copp Adresd/);
    assert.doesNotMatch(message.plainTextBody, /ERP/);
    assert.ok(!message.plainTextBody.includes(patient.documentNumber));
    assert.equal((await http.get("/__qa/welcome-status/" + patientId)).status(), 200);
    console.log("App-only email delivered before first sign-in");

    const bulkEmail = "bulk-" + suffix + "@example.invalid";
    const inactive = "inactive-" + suffix + "@example.invalid";
    const noDocument = "no-document-" + suffix + "@example.invalid";
    const duplicate = "duplicate-" + suffix + "@example.invalid";
    const rows = [
        { ...patient, documentNumber: "B-" + suffix, email: bulkEmail },
        { ...patient, documentNumber: "I-" + suffix, email: inactive, status: "Inactivo" },
        { ...patient, documentNumber: "N-" + suffix, email: null },
        { ...patient, documentNumber: null, email: noDocument },
        { ...patient, email: duplicate },
    ];
    const bulk = await http.post("/api/v1/patients/bulk", { headers, data: { rows } });
    assert.equal(bulk.status(), 200, await bulk.text());
    assert.deepEqual((await bulk.json()).results.map(row => row.success), [true, true, true, true, false]);
    await waitForWelcome(bulkEmail);
    for (const email of [inactive, noDocument, duplicate]) assert.equal(welcome(await inbox(), email).length, 0);

    const otpRequested = await http.post("/api/auth/send-otp", { data: { documentNumber: patient.documentNumber, contactId: "email" } });
    assert.equal(otpRequested.status(), 200, await otpRequested.text());
    const otpMail = (await inbox()).find(mail => mail.to === patient.email && mail.subject !== message.subject);
    const otp = otpMail?.htmlBody.match(/\b[0-9]{6}\b/)?.[0];
    assert.ok(otp, "Captured synthetic OTP required");
    const verified = await http.post("/api/auth/verify-otp", { data: { documentNumber: patient.documentNumber, contactId: "email", otp, application: "app" } });
    assert.equal(verified.status(), 200, "OTP still provisions app access");
    const status = await http.get("/__qa/welcome-status/" + patientId);
    assert.deepEqual(await status.json(), { patientDeliveries: 1, accountDeliveries: 0 });
    assert.equal(welcome(await inbox(), patient.email).length, 1);
    console.log("Bulk eligibility and no duplicate welcome after OTP verified");
} finally {
    await http.dispose();
    await browser.close();
}
