# Letter template: data permission request

For task N00. A generic template for anyone reusing Jharicast; the maintainer's own letters, with
named addressees and follow-up dates, are kept privately. Send from the project mailbox. Address
the head of the agency and copy its Information Officer (सूचना अधिकारी), who by law answers
information requests within 15 days (Right to Information Act 2064, s.7).

---

Subject: Request for permission to use public warning data in a free route-alert service

Dear Sir or Madam,

We are building Jharicast, an open-source software library, and a free web service for travellers in
Nepal. A person enters a route and travel dates; the service shows official warnings for the
districts on that route and notifies them if the situation changes before they travel. Every
screen tells people to follow [agency] and the District Administration Office, and links to your
website.

We would like to use the following data that your public website already shows:

- [DHM: current district warnings (`/home/getAPIData/1`), rain and river watch
  (`/home/getAPIData/3`), and the three-day forecast bulletin (`/mfd/api/...`).]
- [DoR: current road closures shown on navigate.dor.gov.np.]
- [BIPAD: public alerts and incidents from the BIPAD API.]

How we would access it:

- At most one request every 30 minutes per dataset, from one server, identified by a User-Agent
  that names the project and this contact address.
- We store copies for audit and do not resell the data. We credit [agency] wherever the data is
  shown.
- We do not collect or store any personal data from your systems. [DoR only: the closure feed
  includes officers' names and phone numbers; we discard those fields.]

We ask:

1. Written permission to access and display this data as described.
2. Whether an API key or a preferred endpoint exists that we should use instead (for DHM, the
   hydrology API at hydrology.gov.np/gss/api).
3. Any conditions on attribution or redistribution.

[DHM only, as a separate short paragraph: the responsible-disclosure note. Its wording is kept
with the maintainer's sent letters, not in this public repository.]

Thank you for the public service your data provides.

Yours faithfully,
[Name]
[Project, repository URL, contact address]
