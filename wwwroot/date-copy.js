'use strict';
(function(){
  function enhanceDates(root){
    const inputs=[];
    if(root.nodeType===1 && root.matches('input[type="date"]')) inputs.push(root);
    if(root.querySelectorAll) inputs.push(...root.querySelectorAll('input[type="date"]'));
    for(const input of inputs){
      if(input.parentElement?.classList.contains('date-copy-wrap')) continue;
      const wrap=document.createElement('span'); wrap.className='date-copy-wrap';
      const button=document.createElement('button'); button.type='button'; button.className='date-copy-button'; button.textContent='복사';
      const today=document.createElement('button'); today.type='button'; today.className='date-copy-button date-today-button'; today.textContent='오늘';
      const label=input.getAttribute('aria-label') || input.closest('label')?.childNodes[0]?.textContent?.trim() || '날짜';
      button.setAttribute('aria-label',label+' 전체 복사'); button.title='날짜 전체 복사 (YYYY-MM-DD)';
      today.setAttribute('aria-label',label+' 오늘 날짜 입력'); today.title='오늘 날짜 입력';
      input.parentNode.insertBefore(wrap,input); wrap.append(input,today,button);
      today.addEventListener('click',()=>{
        if(input.disabled||input.readOnly)return;
        const parts=new Intl.DateTimeFormat('en-US',{timeZone:'Asia/Seoul',year:'numeric',month:'2-digit',day:'2-digit'}).formatToParts(new Date());
        const value=Object.fromEntries(parts.map(x=>[x.type,x.value]));
        input.value=value.year+'-'+value.month+'-'+value.day;
        input.dispatchEvent(new Event('input',{bubbles:true}));input.dispatchEvent(new Event('change',{bubbles:true}));
      });
      button.addEventListener('click',async()=>{
        if(!input.value){toast('복사할 날짜를 먼저 입력해 주세요.',true);return;}
        try{await navigator.clipboard.writeText(input.value);toast('날짜를 복사했습니다: '+input.value)}
        catch{toast('날짜를 복사하지 못했습니다. 날짜를 선택하고 Ctrl+C를 눌러 주세요.',true)}
      });
    }
  }
  document.addEventListener('paste',event=>{
    const input=event.target;
    if(!(input instanceof HTMLInputElement)||input.type!=='date'||!event.clipboardData||input.disabled||input.readOnly)return;
    const text=event.clipboardData.getData('text/plain').trim();
    const match=text.match(/^(\d{4})[.\/\-](\d{1,2})[.\/\-](\d{1,2})\.?$/)||text.match(/^(\d{4})(\d{2})(\d{2})$/);
    event.preventDefault();
    if(!match){toast('날짜를 YYYY.MM.DD, YYYY-MM-DD, YYYY/MM/DD 또는 YYYYMMDD 형식으로 붙여 넣어 주세요.',true);return;}
    const year=Number(match[1]),month=Number(match[2]),day=Number(match[3]);
    const leap=year%4===0&&(year%100!==0||year%400===0);
    const days=[31,leap?29:28,31,30,31,30,31,31,30,31,30,31];
    if(year<1||month<1||month>12||day<1||day>days[month-1]){toast('올바른 날짜를 붙여 넣어 주세요.',true);return;}
    input.value=match[1]+'-'+String(month).padStart(2,'0')+'-'+String(day).padStart(2,'0');
    input.dispatchEvent(new Event('input',{bubbles:true}));input.dispatchEvent(new Event('change',{bubbles:true}));
  },true);
  document.addEventListener('copy',event=>{
    const input=event.target;
    if(input instanceof HTMLInputElement && input.type==='date' && input.value && event.clipboardData){
      event.clipboardData.setData('text/plain',input.value);event.preventDefault();toast('날짜를 복사했습니다: '+input.value);
    }
  });
  enhanceDates(document);
  new MutationObserver(records=>{for(const record of records)for(const node of record.addedNodes)if(node.nodeType===1)enhanceDates(node)}).observe(document.body,{childList:true,subtree:true});
})();
