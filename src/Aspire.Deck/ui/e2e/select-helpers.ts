import { expect, type Locator } from "@playwright/test";

export async function selectOption(control: Locator, value: string): Promise<void> {
  await control.click();
  await control.page().getByRole("option").locator(`[data-option-value=${JSON.stringify(value)}]`).click();
}

export async function expectSelectedValue(control: Locator, value: string): Promise<void> {
  await expect(control).toHaveAttribute("data-value", value);
}

export async function expectSelectOptions(control: Locator, labels: readonly string[]): Promise<void> {
  await control.click();
  await expect(control.page().getByRole("option")).toHaveText(labels);
  await control.page().keyboard.press("Escape");
}
