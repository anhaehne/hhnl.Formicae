import { useEffect, useRef, useState } from "react";
import type { CustomTaskInputDefinition, CustomTaskScalar } from "../api";

// Preserve editing text until blur; Number() must never turn an empty field into zero.
function normalizedDecimal(text: string) {
  const match = /^([+-]?)(\d*)(?:\.(\d*))?(?:[eE]([+-]?\d+))?$/.exec(text);
  if (!match || !(match[2] || match[3])) return undefined;
  let digits = (match[2] + (match[3] || "")).replace(/^0+/, "");
  if (!digits) return "0";
  let exponent = Number(match[4] || 0) - (match[3]?.length || 0);
  while (digits.endsWith("0")) { digits = digits.slice(0, -1); exponent++; }
  return `${match[1] === "-" ? "-" : ""}${digits}e${exponent}`;
}
function validNumber(text: string) {
  const parsed = Number(text), normalized = normalizedDecimal(text);
  return (normalized === "0" || Number(normalized?.split("e")[1]) >= -28) && text.trim() !== "" && Number.isFinite(parsed) && Math.abs(parsed) <= Number.MAX_SAFE_INTEGER && normalizedDecimal(text) !== undefined && normalizedDecimal(text) === normalizedDecimal(String(parsed));
}
export function ScalarField({ label, type, value, disabled, onChange }: { label: string; type: string; value?: CustomTaskScalar | null; disabled: boolean; onChange: (value: CustomTaskScalar) => void }) {
  const [text, setText] = useState(value == null ? "" : String(value));
  const inputRef = useRef<HTMLInputElement>(null);
  useEffect(() => { if (document.activeElement !== inputRef.current) setText(value == null ? "" : String(value)); }, [value, type]);
  const parsed = Number(text), valid = validNumber(text);
  const error = type === "number" && !valid ? "Enter a finite number that can be represented exactly in this editor (at most 28 decimal places; integers up to 9,007,199,254,740,991)." : "";
  return <div><label><span>{label}</span>{type === "boolean" ? <select aria-label={label} disabled={disabled} value={String(value ?? false)} onChange={event => onChange(event.target.value === "true")}><option value="false">False</option><option value="true">True</option></select> : <input ref={inputRef} aria-label={label} aria-invalid={!!error} disabled={disabled} type="text" inputMode={type === "number" ? "decimal" : undefined} maxLength={16000} value={type === "number" ? text : value == null ? "" : String(value)} onChange={event => { if (type === "number") { const next = event.target.value; setText(next); onChange(validNumber(next) ? Number(next) : next); } else onChange(event.target.value); }} onBlur={() => { if (type === "number") onChange(valid ? parsed : text); }} />}</label>{error && <p role="alert" className="error-text">{error}</p>}</div>;
}
export const initialScalar = (type: string): CustomTaskScalar => type === "boolean" ? false : type === "number" ? 0 : "";
export function CustomTaskSchema({ inputs, disabled, onChange }: { inputs: CustomTaskInputDefinition[]; disabled: boolean; onChange: (inputs: CustomTaskInputDefinition[]) => void }) {
  const update = (index: number, values: Partial<CustomTaskInputDefinition>) => onChange(inputs.map((input, i) => i === index ? { ...input, ...values } : input));
  return <fieldset className="custom-task-schema"><legend>Input schema</legend><p className="muted">Names are case-sensitive template identifiers. Required means a value must be present; empty strings are valid. Omitted values use the default when defined. Numbers allow at most 28 decimal places, must round-trip through JSON without losing decimal precision and stay between −9,007,199,254,740,991 and 9,007,199,254,740,991.</p>
    {inputs.map((input, index) => <fieldset key={index}><legend>Input {index + 1}</legend><div className="form-row"><label><span>Name</span><input aria-label={`Input ${index + 1} name`} required pattern="[A-Za-z][A-Za-z0-9_]{0,63}" disabled={disabled} value={input.name} onChange={event => update(index, { name: event.target.value })} /></label><label><span>Type</span><select aria-label={`Input ${index + 1} type`} disabled={disabled} value={input.valueType} onChange={event => update(index, { valueType: event.target.value as CustomTaskInputDefinition["valueType"], defaultValue: input.defaultValue == null ? undefined : initialScalar(event.target.value) })}><option value="string">String</option><option value="number">Number</option><option value="boolean">Boolean</option></select></label></div>
      <label className="toggle-label"><input aria-label={`Input ${index + 1} required`} type="checkbox" disabled={disabled} checked={input.required} onChange={event => update(index, { required: event.target.checked })} /><span>Required</span></label>
      <label className="toggle-label"><input aria-label={`Input ${index + 1} has default`} type="checkbox" disabled={disabled} checked={input.defaultValue != null} onChange={event => update(index, { defaultValue: event.target.checked ? initialScalar(input.valueType) : undefined })} /><span>Provide default</span></label>
      {input.defaultValue != null && <ScalarField label={`Input ${index + 1} default`} type={input.valueType} value={input.defaultValue} disabled={disabled} onChange={defaultValue => update(index, { defaultValue })} />}
      <button type="button" className="secondary-button" disabled={disabled} onClick={() => onChange(inputs.filter((_, i) => i !== index))}>Remove input {index + 1}</button>
    </fieldset>)}
    <button type="button" className="secondary-button" disabled={disabled || inputs.length >= 32} onClick={() => onChange([...inputs, { name: `input${inputs.length + 1}`, valueType: "string", required: false }])}>Add input</button>
  </fieldset>;
}
