async function connectClients(appContext, app, erpContext, erp, {
  api = 'http://127.0.0.1:51361', instant, requests = [], onMotorResponse, onMotorRequest,
  appOrigin = 'http://127.0.0.1:51362', erpOrigin = 'http://127.0.0.1:51363',
} = {}) {
  await appContext.addInitScript(({ userId, at, origin }) => {
    if (location.origin !== origin) return;
    localStorage.setItem('antares_lang', 'es');
    localStorage.setItem('copp.wellness-privacy.audit.v1.' + at + '.00000000.fixture',
      JSON.stringify({ actor: userId, action: 'accepted', at, acceptedAt: at, scope: 'sensitive-data', version: '2026-11-25-draft.1' }));
  }, { userId: app.userId, at: instant ?? new Date().toISOString(), origin: appOrigin });
  await erpContext?.addInitScript(origin => {
    if (location.origin === origin) localStorage.setItem('copp_lang', 'es');
  }, erpOrigin);
  await Promise.all([[appContext, app], ...(erpContext ? [[erpContext, erp]] : [])].map(([context, session]) =>
    context.route('**/api/**', async route => {
      const req = route.request();
      const url = new URL(req.url());
      const path = url.pathname;
      if (path.startsWith('/api/v1/people/') || path.includes('/year-365')) {
        requests.push([session.userId, req.method(), path]);
        if (await onMotorRequest?.({ req, path, route, session })) return;
        const response = await route.fetch({ url: api + path + url.search });
        if (await onMotorResponse?.({ req, path, response })) return route.abort('connectionreset');
        return route.fulfill({ response });
      }
      const respond = json => route.fulfill({ json });
      if (path === '/api/auth/refresh') return respond({ accessToken: session.accessToken, expiresIn: 1200 });
      if (path === '/api/auth/me') return respond({ id: session.userId, email: 'qa@example.invalid',
        firstName: 'Persona', lastName: 'QA', roles: [],
        permissions: ['Program.Profile360View', 'Program.Export', 'Program.View', 'Patients.View'] });
      if (path === '/api/v1/me/patient-profile') return respond({ patientId: session.patientId,
        firstName: 'Persona', lastName: 'QA' });
      if (path.endsWith('/overview')) return respond({ patient_name: 'Persona QA', enrollment: null,
        streak: null, xp: null, tareas_hoy: [], adherencia_semana: [], evolucion_12_semanas: [],
        scores: null, mediciones_clinicas: null, weaknesses: [], interventions: [], pending_adaptations: 0,
        clinical_reviews: [], daily_checkins: [] });
      if (path === '/api/v1/me/context') return respond({
        userId: session.userId, clinics: session.clinics ?? [], organization: session.organization ?? null, onboardingCompleted: true,
      });
      if (path === '/api/v1/me/account-legal-consents') return respond({
        locale: 'es', enforcementEnabled: true, acceptanceRequired: false, documents: [] });
      if (path === '/api/v1/me/legal-consents') return respond({ locale: 'es', scopes: [] });
      if (path.includes('/biometria/')) return route.fulfill({ status: 404, json: {} });
      return respond([]);
    })));
}

module.exports = { connectClients };
