'use strict';
// Keep decimal digits as text while displaying grouping separators.
function formatMoneyValue(value){const raw=String(value??'').replace(/,/g,'');if(raw===''||!/^\d+(?:\.\d*)?$/.test(raw))return raw;const [whole,...fraction]=raw.split('.');return whole.replace(/\B(?=(\d{3})+(?!\d))/g,',')+(fraction.length?'.'+fraction[0]:'')}
function moneyInputNumber(value){return Number(String(value??'').replace(/,/g,''))}
function validateMoneyInput(input){const raw=input.value.replace(/,/g,'');let error='';if(raw!==''){if(!/^\d+(?:\.\d{1,4})?$/.test(raw))error='숫자와 소수점 네 자리까지 입력해 주세요.';else{const n=Number(raw),min=Number(input.dataset.moneyMin||0);if(!Number.isFinite(n)||n<min)error=min>0?`${min} 이상의 금액을 입력해 주세요.`:'0 이상의 금액을 입력해 주세요.';else if(input.dataset.moneyStep==='1'&&!Number.isInteger(n))error='원 단위 정수로 입력해 주세요.'}}input.setCustomValidity(error)}
function formatMoneyInput(input){const before=input.value,position=input.selectionStart??before.length,rawPosition=before.slice(0,position).replace(/,/g,'').length;const after=formatMoneyValue(before);input.value=after;let caret=0,seen=0;while(caret<after.length&&seen<rawPosition){if(after[caret]!==',')seen++;caret++}try{input.setSelectionRange(caret,caret)}catch{}validateMoneyInput(input)}
document.addEventListener('input',e=>{if(e.target.matches('input[data-money]')&&!e.isComposing)formatMoneyInput(e.target)});
document.addEventListener('compositionend',e=>{if(e.target.matches('input[data-money]'))formatMoneyInput(e.target)});
document.addEventListener('keydown',e=>{const input=e.target;if(!input.matches('input[data-money]')||e.isComposing||input.selectionStart!==input.selectionEnd)return;const pos=input.selectionStart;if(e.key==='Backspace'&&pos>1&&input.value[pos-1]===','){e.preventDefault();input.setRangeText('',pos-2,pos,'end');input.dispatchEvent(new Event('input',{bubbles:true}))}else if(e.key==='Delete'&&input.value[pos]===','&&pos+1<input.value.length){e.preventDefault();input.setRangeText('',pos,pos+2,'end');input.dispatchEvent(new Event('input',{bubbles:true}))}});

document.addEventListener('paste',e=>{
  const input=e.target;if(!(input instanceof HTMLInputElement)||!input.matches('input[data-money]')||input.disabled||input.readOnly||!e.clipboardData)return;
  let text=e.clipboardData.getData('text/plain').replace(/[\u200B-\u200D\u2060\uFEFF]/g,'').trim();if(!text)return;
  if(text.startsWith('"')&&text.endsWith('"'))text=text.slice(1,-1).trim();
  e.preventDefault();
  if(!/^(?:\d+|\d{1,3}(?:,\d{3})+)(?:\.\d{1,4})?$/.test(text)){input.setCustomValidity('숫자 한 개를 복사해 붙여 넣어 주세요.');input.reportValidity();return;}
  input.setRangeText(text.replace(/,/g,''),input.selectionStart??0,input.selectionEnd??input.value.length,'end');
  input.dispatchEvent(new Event('input',{bubbles:true}));
});
