Grocery ERP 1.1.0

This version completes the revised stock, sales and reports.

• Stock: a lot's quantity changes only through its own purchase, sale, return, damage, vendor return, internal use or reviewed count, and it can no longer go negative.
• Purchases: each line is Product, Internal use or Expense, and every stock line gets its own lot. A purchase whose stock was never used can be voided; otherwise return part of it to the vendor.
• Sales: completed bills never change. A return goes back to the original lot only if that lot is unchanged; otherwise it creates a return lot or records damage.
• Profit and loss: calendar and fiscal years lock on 8 January and 8 April and keep their saved figures. Posted expenses, damages and salary payments change only through a dated Edit or Void.
• Screens: Products holds lots, expiry, damage and stock counts, and Vendors has its own page. Tables run to the bottom of the window, and bulk actions are reviewed before they run.
• Data: the old stock ledger is removed and kept in a permanent backup; Settings → Backup can compact the database. Screens are drawn in software to save memory; Settings → Display can use the graphics card instead.

Database schema: 37.
