"use strict";

const previewUi = HelixActivityUI.mount({
  onOpenItem: (_id, button) => {
    const original = button.textContent;
    button.textContent = "Preview only";
    setTimeout(() => { button.textContent = original; }, 1200);
  }
});
previewUi.setContext({
  projectId: "project-alpha",
  projectName: "Alpha Medical Center",
  itemType: "rfi",
  itemLabel: "RFI",
  itemLabelPlural: "RFIs",
  contactId: "contact-current",
  user: {
    id: "directory-current",
    contactId: "contact-current",
    fullName: "Current Helix User",
    company: "Example Company"
  }
});
previewUi.setData({
  scannedAt: "2026-07-21T18:00:00.000Z",
  warnings: [],
  projects: [
    { id: "project-alpha", name: "Alpha Medical Center" },
    { id: "project-beta", name: "Beta Campus Expansion" },
    { id: "project-gamma", name: "Gamma Office — no open items" }
  ],
  people: [
    { key: "contact:contact-current", fullName: "Current Helix User", isCurrent: true },
    { key: "contact:contact-jordan", fullName: "Jordan Reviewer", isCurrent: false }
  ],
  currentPersonKey: "contact:contact-current",
  openQueue: [
    {
      id: "submittal-open-1",
      itemType: "submittal",
      projectId: "project-alpha",
      projectName: "Alpha Medical Center",
      number: "000401",
      title: "Mechanical equipment product data",
      status: "Review",
      contract: "Mechanical",
      responsible: "Ready for Example Company",
      latestActivityAt: "2026-07-20T18:00:00.000Z",
      searchText: "000401 mechanical equipment product data in my court",
      reasons: [],
      queueState: {
        code: "current",
        label: "In court",
        detail: "Required review is ready",
        dueAt: "2026-07-25T18:00:00.000Z"
      },
      queueAssignments: [{
        personKey: "contact:contact-current",
        person: { key: "contact:contact-current", fullName: "Current Helix User", isCurrent: true },
        state: {
          code: "current",
          label: "In court",
          detail: "Required review is ready",
          dueAt: "2026-07-25T18:00:00.000Z"
        }
      }]
    },
    {
      id: "submittal-open-2",
      itemType: "rfi",
      projectId: "project-beta",
      projectName: "Beta Campus Expansion",
      number: "000425",
      title: "Electrical coordination drawings",
      status: "Pending",
      contract: "Electrical",
      latestActivityAt: "2026-07-18T18:00:00.000Z",
      searchText: "000425 electrical coordination drawings coming up",
      reasons: [],
      queueState: {
        code: "upcoming",
        label: "Coming up",
        detail: "Assigned in required review workflow",
        dueAt: "2026-07-30T18:00:00.000Z"
      },
      queueAssignments: [
        {
          personKey: "contact:contact-current",
          person: { key: "contact:contact-current", fullName: "Current Helix User", isCurrent: true },
          state: {
            code: "upcoming",
            label: "Coming up",
            detail: "Assigned in required review workflow",
            dueAt: "2026-07-30T18:00:00.000Z"
          }
        },
        {
          personKey: "contact:contact-jordan",
          person: { key: "contact:contact-jordan", fullName: "Jordan Reviewer", isCurrent: false },
          state: {
            code: "upcoming",
            label: "Coming up",
            detail: "Assigned later in required review workflow",
            dueAt: "2026-07-30T18:00:00.000Z"
          }
        }
      ]
    },
    {
      id: "submittal-open-3",
      itemType: "submittal",
      projectId: "project-beta",
      projectName: "Beta Campus Expansion",
      number: "000433",
      title: "Roofing product data",
      status: "Review",
      contract: "Roofing",
      latestActivityAt: "2026-07-17T18:00:00.000Z",
      searchText: "000433 beta campus roofing product data submittal in my court",
      reasons: [],
      queueState: {
        code: "current",
        label: "In court",
        detail: "Required review is ready",
        dueAt: "2026-07-29T18:00:00.000Z"
      },
      queueAssignments: [{
        personKey: "contact:contact-jordan",
        person: { key: "contact:contact-jordan", fullName: "Jordan Reviewer", isCurrent: false },
        state: {
          code: "current",
          label: "In court",
          detail: "Required review is ready",
          dueAt: "2026-07-29T18:00:00.000Z"
        }
      }]
    }
  ],
  results: [
    {
      id: "rfi-1",
      number: "000179",
      title: "Dock Detail Elevation",
      status: "Pending",
      contract: "Below Grade Waterproofing",
      latestActivityAt: "2026-07-20T18:00:00.000Z",
      searchText: "000179 dock detail elevation below grade waterproofing responded uploaded file edited",
      reasons: [
        { code: "responded", label: "Responded", count: 2 },
        { code: "uploaded", label: "Uploaded file", count: 1 },
        { code: "edited", label: "Edited", count: 3 }
      ]
    },
    {
      id: "rfi-2",
      number: "000310",
      title: "Utility Fiber Connection Update",
      status: "Review",
      contract: "Structured Cabling",
      latestActivityAt: "2026-07-18T18:00:00.000Z",
      searchText: "000310 utility fiber connection structured cabling commented assigned reviewed",
      reasons: [
        { code: "commented", label: "Commented", count: 1 },
        { code: "assigned", label: "Assigned", count: 1 },
        { code: "reviewed", label: "Reviewed", count: 1 }
      ]
    }
  ]
});
previewUi.setPanelOpen(true);
