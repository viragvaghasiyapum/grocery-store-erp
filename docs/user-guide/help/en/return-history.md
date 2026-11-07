<!-- Screen help for a purchase's Return history (topic "return-history"). The app shows this file for the ⓘ button and F1. Bold names in a table's first column must match the labels on screen; see ../README.md. -->
# Return history

See the vendor returns of one purchase and cancel a return posted by mistake.

Cancelling a return puts its units back into stock and its credit back on the vendor's balance, and adds the tax back on the day it is cancelled. If the returned lot was emptied and deleted since, the units go into a new lot with that lot's purchase price, tax, batch, expiry and prices.

## Table

| Column | What it means |
|---|---|
| **Document** | The return's number. |
| **Date** | The day of the return. |
| **Credit** | What the vendor credited for it. |
| **Cost value** | What the returned stock had cost. |
| **Cancelled** | Ticked once the return has been cancelled. |
| **Cancelled date** | The day it was cancelled. |

## Buttons

| Button | What it does |
|---|---|
| **Cancel return** | Asks for a reason, then cancels the selected return. |
| **Previous**, **Next** | Step through the returns when there are more than fit. |
| **Close** | Closes the window. |
