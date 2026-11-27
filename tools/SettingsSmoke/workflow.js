
window.smokeResult=null;
(async()=>{
 const checks=[];const assert=(value,label)=>{if(!value)throw Error(label);checks.push(label);};
 const idle=async()=>{for(let i=0;i<100&&nativeBusy;i++)await new Promise(r=>setTimeout(r,40));assert(!nativeBusy,'native save completed');};
 assert(model.libraries.length===0,'fresh installation has no demo libraries');
 openLibraryDialog(false,false);libraryForm.elements.name.value='测试术语库';libraryForm.requestSubmit();await idle();
 assert(model.libraries.length===1,'create library persisted');
 const library=model.libraries[0];ui.library=library.id;
 library.terms=[{source:'sensor',target:'传感器',direction:'en>zh-Hans',enabled:true}];await persist('保存测试词条');await idle();
 const snapshot=await nativeCall('bootstrap');assert(snapshot.document.libraries[0].terms[0].target==='传感器','real database returns edited terminology');
 for(let i=0;i<6;i++)model.libraries.push({id:crypto.randomUUID(),name:'测试库'+i,kind:'pair',direction:'en>zh-Hans',hits:0,terms:[]});
 await persist();openPage('knowledge');await new Promise(r=>requestAnimationFrame(r));
 const list=document.querySelector('[data-scroll-list="libraries"]');
 assert(list.classList.contains('bounded-list')&&list.style.maxHeight,'more than five libraries scroll');
 const combo=model.presets[0];combo.ids=[library.id];await persist();
 openPage('combinations');const input=document.querySelector('input[name="library"][value="'+model.libraries[1].id+'"]');
 input.checked=true;input.dispatchEvent(new Event('change',{bubbles:true}));
 assert(!model.presets[0].ids.includes(input.value),'combination selection remains draft');
 document.querySelector('[data-action="save-combination"]').click();await idle();
 assert(model.presets[0].ids.length===2,'combination save commits selected libraries');
 openPage('knowledge');ui.library=library.id;
 deleteRecords(true,false,[library.id]);await idle();
 assert(byId(library.id).deletedAt,'soft deletion persisted');
 handleTrash('restore-library',library.id);await idle();
 assert(!byId(library.id).deletedAt,'library restored');
 await commitRetention({shortWords:12,mediumWords:55,shortSeconds:95,mediumSeconds:310,longSeconds:610});
 const updated=await nativeCall('bootstrap');assert(updated.retention.shortWords===12&&updated.retention.longSeconds===610,'retention writes native settings');
 await commitRetention({...retentionDefaults});
 const child=document.getElementById('model-frame').contentWindow;
 openNativePage('models/assistant');await new Promise(r=>setTimeout(r,150));
 assert(child.document.querySelector('h1').textContent==='导入助手模型','model sidebar opens correct subpage');
 assert(child.document.querySelector('[data-kind="assistant"]'),'assistant has independent connection form');
 openNativePage('models/translation');await new Promise(r=>setTimeout(r,100));
 child.document.querySelector('[data-action="method"][data-value="llm"]').click();
 child.document.querySelector('[data-action="method"][data-value="api"]').click();
 assert(child.document.querySelector('[data-kind="api"]'),'remote API configuration is interactive');
 assert(!document.body.innerText.includes('译文预览'),'translation preview removed');
// Version rollback appends a revision rather than erasing history.
 openPage('knowledge');ui.library=library.id;
 const beforeVersion=history(byId(library.id));
 byId(library.id).terms[0].target='感应器';recordVersion(byId(library.id),beforeVersion,'测试修改',1);await persist();
 openHistory();showRestore(1);document.getElementById('restore-form').requestSubmit();await idle();
 assert(byId(library.id).terms[0].target==='传感器'&&history(byId(library.id)).length===3,'version restore appends history and restores original terminology');
 document.getElementById('history-dialog').close();
 const secondId=model.presets[0].ids.find(id=>id!==library.id);
 deleteRecords(true,false,[library.id,secondId]);await idle();
 const combination=model.presets[0];deleteRecords(false,false,[combination.id]);await idle();
 handleTrash('restore-combo',combination.id);
 assert(trashInputs().length===2&&trashInputs().every(item=>item.checked),'combination restore selects all deleted libraries by default');
 trashInputs()[1].checked=false;trashInputs()[1].dispatchEvent(new Event('change',{bubbles:true}));
 document.querySelector('#trash-confirm').requestSubmit();await idle();
 assert(!combination.deletedAt&&!byId(library.id)?.deletedAt,'combination restored with selected libraries');
 const remaining=combination.ids.map(byId).find(item=>item?.deletedAt);
 if(remaining){deleteRecords(true,true,[remaining.id]);await idle();assert(!combination.ids.includes(remaining.id),'permanent library deletion prunes combination references');}
 openPage('knowledge');ui.library=library.id;
 const csv=parseGlossary('term,translation,source_lang,target_lang,note\ncamera,相机,en,zh-Hans,测试备注','csv','fixture.csv');
 prepareDraft(byId(library.id),csv,'imported');ui.assistantStep='review';openPage('assistant');saveDraft();await idle();
 assert(byId(library.id).terms.some(term=>term.source==='camera'&&term.note==='测试备注'),'CSV preview confirmation preserves notes and writes version');

openNativePage('models/translation');await new Promise(r=>setTimeout(r,50));
 child.document.querySelector('[data-action="method"][data-value="llm"]').click();
 child.document.querySelector('[data-action="expand"][data-value="connection"]').click();
 const localForm=child.document.querySelector('#connection');
 localForm.elements.address.value=window.fixtureAddress;localForm.elements.address.dispatchEvent(new Event('input',{bubbles:true}));
 localForm.elements.model.value='fixture-draft';localForm.elements.model.dispatchEvent(new Event('input',{bubbles:true}));
 localForm.querySelector('[data-mode="test"]').click();
 for(let i=0;i<200&&child.document.querySelector('#connection button').disabled;i++)await new Promise(r=>setTimeout(r,25));
 assert(child.document.querySelector('#connection').elements.model.value==='fixture-draft','testing a connection preserves unsaved form values');
child.discardModelDraft();
const beforeModel=await nativeCall('model-state');
 const helper=await nativeCall('configure-model',{kind:'assistant',address:window.fixtureAddress,model:'fixture-assistant'});
 assert(helper.mode===beforeModel.mode,'assistant configuration does not switch translation mode');
 assert(helper.configs.some(c=>c.kind==='assistant'),'assistant configuration persisted independently');
 const generated=await nativeCall('assistant',{task:'generate_terminology',topic:'测试传感器',details:'仅测试协议',allowed_pairs:['en>zh-Hans','zh-Hans>en']});
 assert(generated.entries.length===2,'structured helper generation returns two directions');
 const detected=await nativeCall('assistant',{task:'detect_languages',entries:[{index:3,source:'sensor',target:'传感器'}]});
 assert(detected.entries[0].index===3,'language detection preserves row identity');
 const local=await nativeCall('configure-model',{kind:'llm',address:window.fixtureAddress,model:'fixture-translation'});
 assert(local.mode==='llm'&&local.currentLocal==='fixture-translation','translation model connection activates only after validation');
 const language=await nativeCall('languages',{source:'ja',target:'en'});
 assert(language.source==='ja'&&language.target==='en','translation language settings persisted');
 const downloaded=await nativeCall('download-model',{tag:'qwen2.5:1.5b'});
 assert(downloaded.currentLocal==='qwen2.5:1.5b','verified download automatically configures model');
 assert(downloaded.llmRecommendations.length===4&&downloaded.llmRecommendations.find(row=>row.tag==='qwen2.5:1.5b').cached,
   'recommended LLM catalog reflects the locally confirmed download');
 assert(downloaded.offlineLanguagePack.totalBytes>490000000&&downloaded.freeDownloadBytes>0,
   'shared language download and disk space estimates are provided by the native app');
 let rejectedDownload=false;
 try{await nativeCall('download-model',{tag:'qwen2.5:3b'});}catch(error){rejectedDownload=error.message.includes('试译未通过');}
 const afterRejectedDownload=await nativeCall('model-state');
 assert(rejectedDownload&&afterRejectedDownload.currentLocal==='qwen2.5:1.5b'&&
   !afterRejectedDownload.configs.some(row=>row.model==='qwen2.5:3b')&&afterRejectedDownload.llmRecommendations.find(row=>row.tag==='qwen2.5:3b').cached,
   'downloaded model failing trial stays reusable and leaves the previous selection active');
 const helperId=downloaded.configs.find(c=>c.kind==='assistant').id;
 const removed=await nativeCall('remove-models',{ids:[helperId]});
 assert(!removed.configs.some(c=>c.kind==='assistant')&&removed.currentLocal==='qwen2.5:1.5b','removing assistant preserves translation model');


 const catalog=await nativeCall('model-state');
 assert(catalog.ocrRecommendations.length===4&&catalog.ocrRecommendations.every(profile=>profile.files.every(file=>file.sha256.length===64)),'four real OCR profiles include verified file checksums');
 openNativePage('models/ocr');await new Promise(r=>setTimeout(r,100));
 child.document.querySelector('[data-action="expand"][data-value="downloads"]').click();
 assert(child.document.querySelectorAll('[data-action="download-ocr"]').length===4,'OCR recommendations use real download actions');
 assert(child.document.querySelector('[data-action="download-directory"]'),'recommendations expose the configured download directory');
 child.document.querySelector('[data-action="expand"][data-value="import"]').click();
 child.document.querySelector('[data-action="helper"]').click();await new Promise(r=>setTimeout(r,100));
 assert(child.document.querySelector('[data-action="return-import"]'),'import helper has a return-to-import navigation');
 child.document.querySelector('[data-action="return-import"]').click();await new Promise(r=>setTimeout(r,100));
 assert(child.document.querySelector('[data-action="import"]'),'return navigation restores OCR import section');
 openPage('preferences');await new Promise(r=>setTimeout(r,150));
 assert(document.getElementById('preference-collect')&&!document.getElementById('preference-collect').checked,'passive preference collection starts disabled');
 const preferenceForm=document.getElementById('new-preference');
 preferenceForm.elements.source.value='en';preferenceForm.elements.target.value='zh-Hans';preferenceForm.elements.rule.value='保留原文数字编号';preferenceForm.requestSubmit();
 for(let i=0;i<100;i++){if((await nativeCall('preferences')).document.rules.length)break;await new Promise(r=>setTimeout(r,25));}
 const preferenceSaved=await nativeCall('preferences');
 assert(preferenceSaved.document.rules.length===1&&preferenceSaved.document.rules[0].rule==='保留原文数字编号','manual style preference persists separately from terminology');
 assert(model.libraries.every(library=>library.terms.every(term=>term.source!=='保留原文数字编号')),'style rule does not create a terminology entry');

 if(window.realModelCase){
   const englishChinese=await nativeCall('download-languages',{languages:['en','zh-Hans']});
   const pack=englishChinese.configs.find(config=>config.id==='common-languages-m2m100-7c1b2620');
   assert(pack.keys.length===2&&pack.keys.includes('zh-Hans:en'),'real shared-core download enables only the selected bidirectional language pair');
   const reversed=await nativeCall('languages',{source:'zh-Hans',target:'en'});
   assert(reversed.source==='zh-Hans'&&reversed.target==='en','real local package supports persisted Chinese-to-English direction');
   const japanese=await nativeCall('download-languages',{languages:['en','zh-Hans','ja']});
   assert(japanese.configs.find(config=>config.id===pack.id).keys.length===6,'enabling an additional language reuses the core and tests all new directions');
   let invalid=false;try{await nativeCall('languages',{source:'ko',target:'en'});}catch{invalid=true;}
   assert(invalid,'unselected language direction is rejected even when shared weights support it');
 }
// Continues the original native workflow with the new UX contracts.
openPage('preferences');await new Promise(r=>setTimeout(r,100));
assert(!document.getElementById('preference-scope'),'preferences no longer require a current-scene text field');
const enabled=document.getElementById('preferences-enabled');enabled.checked=false;enabled.dispatchEvent(new Event('change',{bubbles:true}));
await persistPreferences();assert((await nativeCall('preferences')).document.enabled===false,'preference master switch is persisted');
document.getElementById('preferences-enabled').checked=true;document.getElementById('preferences-enabled').dispatchEvent(new Event('change',{bubbles:true}));
document.querySelector('.nav [data-page="storage"]').click();await new Promise(r=>setTimeout(r,50));
assert(leaveDialog.open&&ui.page==='preferences','leaving modified preferences prompts before navigation');
leaveDialog.close('stay');await new Promise(r=>setTimeout(r,30));assert(ui.page==='preferences'&&preferenceDirty,'continue editing preserves pending changes');
document.querySelector('.nav [data-page="storage"]').click();await new Promise(r=>setTimeout(r,30));leaveDialog.close('discard');await new Promise(r=>setTimeout(r,100));
assert(ui.page==='storage'&&!preferenceDirty&&(await nativeCall('preferences')).document.enabled===false,'discard changes does not overwrite saved preferences');
await refreshStorage();assert(storageState.groups.some(row=>row.name==='本机 LLM'),'storage reports managed model files');
await nativeCall('basic-settings',{values:{dwellMs:450,ocrEnabled:false}});const basic=await nativeCall('bootstrap');assert(basic.basic.dwellMs===450,'capture controls persist actual native settings');

openPage('retention');await commitRetention({shortWords:12,mediumWords:55,shortSeconds:95,mediumSeconds:310,longSeconds:610});
document.querySelector('[data-action="reset-retention"]').click();
assert(retentionDraft.shortWords===10&&(await nativeCall('bootstrap')).retention.shortWords===12,'restore defaults changes draft until saved');
document.querySelector('[data-action="cancel-retention"]').click();assert(retentionDraft.shortWords===12,'cancel restores saved retention settings');
await commitRetention({...retentionDefaults});

const exportRoundtrip=parseGlossary("term,translation,source_lang,target_lang,note,reference,enabled,jianyi_escaped_fields\n\"'=SUM(1,2)\",\"literal\",en,zh-Hans,\"note\",\"origin\",false,term",'csv','export.csv');
assert(exportRoundtrip[0].source==='=SUM(1,2)'&&!exportRoundtrip[0].enabled&&exportRoundtrip[0].reference==='origin','safe CSV export markers preserve exact terms and metadata on reimport');
const activeBefore=model.active,extraCombo={id:crypto.randomUUID(),name:'组合编辑隔离',mode:'manual',ids:[]};model.presets.push(extraCombo);await persist();
openPage('combinations');await requestCombo(extraCombo.id);
assert(model.active===activeBefore&&preset().id===extraCombo.id,'selecting a combination for editing does not silently activate it');
const member=document.querySelector('input[name="library"]');member.checked=true;member.dispatchEvent(new Event('change',{bubbles:true}));
await saveCurrentCombination();assert(model.active===activeBefore&&model.presets.find(row=>row.id===extraCombo.id).ids.length===1,'saving another combination preserves the current active combination');
ui.editCombo=activeBefore;
openPage('combinations');document.querySelector('[data-action="new"][data-value="empty"]').click();
form.elements.name.value='空白新组合';form.requestSubmit();await idle();
assert(preset().name==='空白新组合'&&preset().ids.length===0&&model.active===activeBefore,'new combination starts empty and does not silently activate');
ui.editCombo=activeBefore;

openPage('knowledge');ui.library=library.id;ui.filter='all';ui.search='';render();
const firstTerm=document.querySelector('[name="selected-term"]');firstTerm.checked=true;firstTerm.dispatchEvent(new Event('change',{bubbles:true}));
document.querySelector('[data-action="disable-terms"]').click();await idle();assert(byId(library.id).terms[0].enabled===false,'bulk disabling terms changes actual library state');
ui.selectedTerms.add(0);render();document.querySelector('[data-action="enable-terms"]').click();await idle();assert(byId(library.id).terms[0].enabled===true,'bulk enabling terms restores active state');
assert(changeRows([{source:'x',target:'甲',direction:'en>zh-Hans'}],[{source:'x',target:'乙',direction:'en>zh-Hans'}],byId(library.id))[0].type==='修改','history compares term changes rather than only version counts');

const originalTerm=byId(library.id).terms[0];
prepareDraft(byId(library.id),[{source:originalTerm.source,target:originalTerm.target,direction:entryDirection(originalTerm,byId(library.id))}],'imported');
assert(ui.draft.rows[0].status==='duplicate'&&!ui.draft.rows[0].included,'identical imports are skipped by default');
prepareDraft(byId(library.id),[{source:originalTerm.source,target:'测试更新译法',direction:entryDirection(originalTerm,byId(library.id))},{source:'processor',target:'处理器',direction:'en>zh-Hans'},{source:'controller',target:'控制器',direction:''}],'imported');
ui.assistantStep='review';openPage('assistant');
assert(ui.draft.rows[0].status==='conflict'&&ui.draft.rows[0].resolution==='skip','conflicting translations preserve original by default');
const resolution=document.querySelector('[data-draft-resolution="0"]');resolution.value='update';resolution.dispatchEvent(new Event('change',{bubbles:true}));
const targetInput=document.querySelector('[data-draft-field="target"][data-row="1"]');targetInput.value='处理单元';targetInput.dispatchEvent(new Event('input',{bubbles:true}));targetInput.dispatchEvent(new Event('change',{bubbles:true}));
assert(ui.draft.rows[1].target==='处理单元','preview translation can be edited before saving');
await saveDraft();await idle();
assert(byId(library.id).terms.some(row=>row.source==='processor'&&row.target==='处理单元'),'valid checked preview rows save even when other rows require language confirmation');
assert(byId(library.id).terms[0].target==='测试更新译法','explicit import conflict resolution updates the existing entry');

const choices=await nativeCall('model-list',{kind:'llm',address:fixtureAddress,model:''});assert(choices.includes('fixture-translation'),'model list reads actual compatible endpoint');
const helperConfig=await nativeCall('configure-model',{kind:'assistant',address:fixtureAddress,model:'fixture-assistant'});
const checking=nativeCall('check-model',{id:'assistant'});openPage('knowledge');
assert(!document.getElementById('global-task').hidden,'ongoing model task remains visible after navigating away');
assert(document.querySelector('#global-task [data-task="cancel"]')&&!document.querySelector('#global-task [data-task="dismiss"]'),
 'a running task offers cancellation and cannot be dismissed');
const duplicates=await Promise.allSettled(Array.from({length:25},()=>nativeCall('check-model',{id:'assistant'})));
assert(duplicates.every(result=>result.status==='rejected'&&result.reason.message.includes('当前任务')),
 'rapid repeated starts are rejected while a task is active');
const checked=await checking;assert(checked.configs.find(row=>row.id==='assistant').lastCheck==='passed','model status records a completed capability check');
readyModels=checked;child.syncNativeModels(checked);
openNativePage('models/translation');await new Promise(r=>setTimeout(r,80));
child.document.querySelector('[data-action="method"][data-value="api"]').click();
child.discardModelDraft();child.document.querySelector('[data-action="method"][data-value="llm"]').click();
child.document.querySelector('[data-action="expand"][data-value="connection"]').click();
const realForm=child.document.getElementById('connection');realForm.elements.address.value=fixtureAddress;realForm.elements.model.value='fixture-via-form';realForm.elements.configId.value='';
realForm.elements.model.dispatchEvent(new Event('input',{bubbles:true}));realForm.requestSubmit(realForm.querySelector('[data-mode="save"]'));
for(let i=0;i<100;i++){await new Promise(r=>setTimeout(r,20));if((await nativeCall('model-state')).configs.some(row=>row.model==='fixture-via-form'))break;}
assert((await nativeCall('model-state')).configs.some(row=>row.model==='fixture-via-form'),'model form submits and persists configuration through actual UI');
child.document.querySelector('[data-action="method"][data-value="api"]').click();
const provider=child.document.getElementById('provider');provider.value='deepseek';provider.dispatchEvent(new Event('change',{bubbles:true}));
assert(child.document.querySelector('[name="address"]').value==='https://api.deepseek.com/chat/completions','provider preset uses API address rather than account console');
assert(child.hasModelDraft(),'connection edits remain unsaved until explicit submission');child.discardModelDraft();
openPage('trash');ui.trashSearch='不存在的已删除条目';render();assert(!app.querySelector('[data-action="restore-library"]'),'trash search filters visible deleted content');ui.trashSearch='';
openPage('preferences');await new Promise(r=>setTimeout(r,100));

 openNativePage('retention');window.smokeResult={ok:true,checks};
})().catch(error=>window.smokeResult={ok:false,error:error.stack});



