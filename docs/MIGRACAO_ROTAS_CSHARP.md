# Migração das rotas JRTools para comandos C# (piloto da versão 2.0 do Bis)

Contexto: issue [iuri1/Revit-Orchestrator#2](https://github.com/iuri1/Revit-Orchestrator/issues/2) — na 2.0 o
Bis não depende do pyRevit. Este piloto (branch `piloto-rotas-csharp`) faz a fase 5 em pequeno: algumas rotas do
`Rotas.pushbutton/script.py` (IronPython) viram comandos do add-in, com **o mesmo nome, os mesmos nomes de
argumentos e o mesmo JSON de resposta**, para o `bis_comum.rota` trocar uma pela outra sem mexer nos chamadores.

## 1. `tx_seguro` no add-in

Porte da rota `tx_seguro` (script.py, `def tx_seguro`). Opcional **por chamada**, em qualquer comando de escrita
(`ExecutionKind.ModelWrite`), pela chave reservada `tx_seguro` nos `params`:

```jsonc
{ "command": "set_view_scale", "params": { "view_ids": [123], "scale": 50,
  "tx_seguro": true } }                                   // ou:
  "tx_seguro": { "nome": "Bis: escala", "resolver_contem": ["manter unidos"] }
```

- O dispatcher (`RevitMCPExternalEventHandler.RunSingleTxSeguro`) tira a chave dos params, abre a Transaction
  (nome = `nome` ou `MCP: <comando>`), instala o `IFailuresPreprocessor` ESTRITO (`TxSeguro.cs`) e
  `SetClearAfterRollback(true)`.
- Regras (em `TxSeguroPolicy.cs`, puras e testadas): aviso → `DeleteWarning`; erro cujo texto contém algum
  `resolver_contem` (sem maiúsculas), com resoluções, e menos de 3 passes já feitos → `ResolveFailure` +
  `ProceedWithCommit` (registrado como resolvido); qualquer outro erro → `ProceedWithRollBack` **sem diálogo**
  e a mensagem vai para `falhas`; rollback ganha de commit dentro do mesmo passe; exceção lendo as mensagens →
  rollback.
- Resposta (`data`) no formato da rota: `{status, falhas: [{erro, ids}..., {n_resolvidos}], resultado,
  resultado_descartado}`. Rollback **não** vira envelope de erro (igual à rota: `ok: true`, `status:
  "RolledBack"`). Exceção do comando → rollback + envelope de erro (a rota também levanta).
- `dryRun` + `tx_seguro`: executa, desfaz e responde `status: "RolledBack"`, `dryRun: true`.
- Recusado com `bad_request`: em comando de leitura/UI (não há transação) e dentro de `batch` (os passos dividem
  uma transação só).
- Sem a chave, nada muda: o comportamento atual (inclusive `SuppressWarningsOnCommit`) segue igual.

## 2. Rotas portadas (13 comandos novos, ferramentas `revit_<nome>`)

| Rota / comando | Tipo | Argumentos (iguais à rota) | Resposta (igual à rota) |
|---|---|---|---|
| `create_drafting_view` | escrita | `name`, `scale`=10 | `{id, name, scale}` ou `{error}` sem tipo Drafting |
| `set_view_scale` | escrita | `view_ids`, `scale` | `[{id, scale} \| {id, error}]` |
| `set_viewport_centers` | escrita | `items=[{id, x, y}]` (m) | `[{id, ok: true}]` |
| `viewport_boxes` | leitura | `sheet_id` | `[{id, view, view_id, box_m{min,max}, w_m, h_m, center_m}]` por x |
| `place_schedule_on_sheet` | escrita | `sheet_id`, `schedule_id`, `x_m`, `y_m` | `{id, bbox_m{min,max} \| null}` |
| `rooms_bbox` | leitura | `level_name`, `nomes_contem`=None | `[{id, nome, min, max}]` |
| `room_areas` | leitura | `ids`=None | `[{id, nome, area_m2, nivel}]` |
| `element_bbox_m` | leitura | `ids` | `{"<id>": {min, max, wall_normal_deg?} \| null}` |
| `dims_list` | leitura | `view_ids` | `{"<vista>": [{id, cm, dir, uv?}]}` |
| `set_annotation_crop` | escrita | `view_ids`, `active`=True, `offset_m`=0 | `[{view, ok} \| {view, error}]` |
| `rename_views` | escrita | `items=[{id, nome}]` | `[{id, nome}]` (sufixo ` (2)`..` (20)`) |
| `list_text_types` | leitura | — | `[{id, name, size_mm, color}]` por nome |
| `list_filled_region_types` | leitura | — | `["nome", ...]` ordenado |

Fidelidade à rota (em `RouteCompat.cs`, testado): metros por fora e pés por dentro com as mesmas contas da rota
(`_FT = 1/0.3048`, `_m(v) = round(v/_FT, 3)`), arredondamento do Python 2 (meio para longe do zero; o padrão do
C# é o bancário), verdade do Python para `active`/`offset_m` (`"false"` é verdadeiro, `0` é falso), `int()`
truncando a escala, `str(ex)[:80]` nos erros por item, ids ecoados como vieram, ordenação por ponto de código.

Diferenças pequenas e conscientes (não mudam o uso):
- Número inteiro sai como `2` e não `2.0` no JSON (depois do `json.loads` é igual na comparação).
- Texto dos erros por item é a mensagem do Revit/.NET, não a do IronPython.
- `rooms_bbox` pula ambiente sem caixa (a rota quebraria); aceita `nomes_contem` como texto único.
- `element_bbox_m`, `viewport_boxes`, `rename_views`: elemento inexistente → erro `not_found` (a rota levanta
  `AttributeError`; nos dois casos o `bis_comum` dá `ErroRota`).

## 3. Equivalência das que JÁ existem em C# (sem mudar nada nesta etapa)

| Rota Python | Comando C# | Igual | Diferente | Falta no C# para substituir |
|---|---|---|---|---|
| `delete_elements(ids, tx_name)` | `delete_elements` | apaga por id | Rota apaga **um a um** com try por id (um id que falha, ex. a vista ativa, vai para `nao_apagados` e os outros saem); transação própria com nome; preprocessor que dispensa aviso e registra as mensagens. C# faz `doc.Delete(lista)` de uma vez: um id ruim derruba a chamada toda (rollback); sem tratamento de aviso. Resposta: rota `{apagados, ids, nao_apagados[{id,motivo}], levados_junto, commit, failures[{msg,n}]}` × C# `{requested, deleted, deletedIds, changeSummary}`. | laço por id com motivo; `tx_name`; aviso dispensado (o `tx_seguro` cobre); `levados_junto`/`failures`; **proteger a vista ativa** explicitamente (pular e reportar, em vez de depender da exceção). |
| `place_view_on_sheet(sheet_id, view_id, x_m, y_m, viewport_type_id)` | `place_view_on_sheet` | `Viewport.Create` | **Unidades**: rota em metros (`x_m/0.3048`); C# usa `location{x,y}` **em pés, sem conversão** (e padrão 1.0/0.75 pé). C# confere `CanAddViewToSheet` antes. Rota troca o tipo do viewport (**título do viewport**) com `viewport_type_id`. Resposta: rota `{id, w_m, h_m, center_m}` × C# `{viewportId, sheetId, viewId}`. | `x_m/y_m` em metros; `viewport_type_id`; caixa/centro em metros na resposta. |
| `create_sheet(number, name, title_block_id, ref_sheet_id)` | `create_sheet` | `ViewSheet.Create` + número + nome | Carimbo: rota por **id** ou copiando o da folha `ref_sheet_id`; C# por **nome** (`titleBlockName`). Número repetido: rota **falha** (rollback); C# **engole o erro** e fica com o número automático. Resposta `{id, number, name}` × `{id, sheetNumber, name}`. | `title_block_id`, `ref_sheet_id`; erro em número repetido; chaves `number`. |
| `duplicate_view(view_id, nome, modo)` | `duplicate_view` | `View.Duplicate` | Padrão: rota `detalhamento` (WithDetailing), C# `Duplicate` (sem detalhes). Modos com nomes PT (`simples/detalhamento/dependente`) × enum. Rota usa a vista do argumento se não vier `view_id`; erros voltam como dado (`{error}`, `{id, nome_erro}`); C# lança. Resposta `{id, nome, de}` × `{id, name, viewType, sourceViewId}`. | `modo` PT com padrão WithDetailing; vista padrão; `nome_erro`; chaves PT. |
| `create_3d_view(name, scale, template_id, section_box_m, eye_dir, detail_level)` | `create_3d_view` | cria View3D | C# **duplica a vista 3D ativa** quando ela é 3D (herda caixa de corte/filtros); rota sempre cria isométrica nova. Rota: nome limpo de caracteres proibidos + sufixo ` (n)`, escala (50), template, caixa de corte em **metros**, orientação `eye_dir`, nível de detalhe (Fine). C# só `viewName`. Resposta `{id, name, scale}` × `{id, name, viewType, duplicatedFrom}`. | todos os argumentos da rota; não duplicar a ativa; nome único. |
| `list_rooms(view, with_equipment)` | `list_rooms` | lista ambientes com área > 0 | Rota: só o **nível da vista** passada, com **equipamentos hidráulicos** detectados (core PontosHidraulicos), `center_m`, área em m², ordenado por número/nome, resposta `{level, count, rooms}`. C#: todos os níveis, área em pés² e `areaMetric`, perímetro, departamento, `{count, rooms}`. | filtro pelo nível da vista; `center_m`; detecção de equipamentos (lógica grande do core: fica para outra fase). |
| `create_note(x, y, text, width_mm, leaders_to, type_id)` → `create_text_note` | `create_text_note` | `TextNote.Create` | **Largura**: rota em **mm de papel** (`width_mm/304.8`, limitada a `GetMinimum/MaximumWidthLimit`); C# `width` em unidades de modelo (m→pés, padrão 0.5): nota gigante ou recusada (ver AGENTS.md: largura em modelo gera nota sem geometria). Tipo: rota `type_id` ou o **tipo padrão** do documento; C# o primeiro TextNoteType. Rota cria **linhas de chamada** (`leaders_to`). Resposta `{note, leaders?}` × `{id, viewId, text}`. | largura em mm de papel com limites; `type_id`/tipo padrão; leaders; `x,y` soltos. |
| `hide_in_view(view, ids)` | `hide_elements_in_view` | `View.HideElements` | Rota filtra `CanBeHidden` **um a um** e devolve `{hidden:[ids], skipped:[...]}`; C# esconde a lista de uma vez (um elemento que não pode ser oculto derruba tudo) e devolve `{hidden: n, viewId}`. Vista: rota pelo argumento `view` (`{"$view": id}`); C# `viewId` ou ativa. | filtro `CanBeHidden` por elemento; listas `hidden/skipped`. |
| `list_walls(view)` | `spatial_get_walls` (só HTTP) | paredes com geometria em metros | Rota: paredes **visíveis na vista**, tipo, largura, extremos do eixo, comprimento, **ambientes dos dois lados**, normal. C#: todas as paredes do documento, contorno (anel) da planta, z0/z1, nível, `isExternal`. Propósitos diferentes. | comando novo `list_walls` no formato da rota (filtro da vista + ambientes dos lados). |

Resumo: nenhuma das 9 é drop-in hoje. As mais próximas (`create_sheet`, `place_view_on_sheet`,
`hide_in_view`) pedem só argumentos/unidades/resposta; `create_note` e `list_rooms` dependem de lógica que
mora em outros arquivos do JRTools (largura de papel + leaders; core de equipamentos). Caminho recomendado:
comandos novos com o nome da rota no namespace das rotas (ex.: `rota_delete_elements`) ou opção `formato:
"rota"` nos existentes — sem quebrar quem já usa o formato atual.

## 4. Cliente (`bis_comum.rota`, repo Revit-Orchestrator)

Desligado por padrão. Liga com `ORQUESTRADOR_ROTAS_CSHARP=1` (vale sobre tudo; `0` força desligado) ou
`"rotas_csharp": true` no `config.json` do Orquestrador (`%LOCALAPPDATA%\Orquestrador\config\config.json`).

Ligado, para as 13 funções da tabela `ROTAS_CSHARP` (botão `Rotas`/`PontosHidraulicosLote`):
1. `ping` no add-in (uma vez por processo); fora do ar → rota pyRevit.
2. `args` (`['$doc', vista, a1, ...]`) + `kwargs` viram params pelos nomes da assinatura. Valor especial
   (`{"$view"}`, `{"$element"}`), `cut_marker`, argumento a mais ou kwarg desconhecido → rota pyRevit.
3. Chama `POST /mcp` como o `mcp()`. Escrita vai sempre com `tx_seguro` (nome = o `tx` do chamador);
   `Committed` → devolve `resultado`; `RolledBack` → `ErroRota` com as `falhas` (a rota devolveria o resultado
   como se tivesse gravado — ver AGENTS.md, "transacao revertida volta ok").
4. `unknown_command` (add-in antigo), `unauthorized`, `bad_request`/`invalid_parameter` → rota pyRevit (nada
   rodou). Outro erro → `ErroRota` igual à rota. Escrita sem resposta → `ErroRota` ("confira o modelo"), nunca
   repete na rota.
5. A rota `tx_seguro` embrulhando uma rota de escrita portada também vai para o C# e devolve o formato da
   `tx_seguro` (`status`, `falhas`, `resultado`, `resultado_descartado`) inalterado.

Testes: `orquestrador/test_rotas_csharp.py` (transporte simulado, sem Revit).

## 5. Como testar ao vivo (dono) e voltar atrás

1. Salvar e **fechar o Revit** (a DLL instalada fica travada com ele aberto).
2. Guardar a instalação atual: copiar `%APPDATA%\Autodesk\Revit\Addins\2026\RevitMCPAddin.dll` e
   `RevitMCP.Core.dll` para uma pasta de backup.
3. No worktree: `cd E:\RevitMCPServer-piloto-csharp\src\RevitAddin` e `dotnet build` (sem
   `-p:DeployToRevit=false`, o build copia as DLLs para a pasta Addins do ano). Para 2025:
   `dotnet build -p:RevitVersion=2025`.
4. Abrir o Revit num projeto de teste; `Invoke-RestMethod http://127.0.0.1:7891/health` deve mostrar a branch
   `piloto-rotas-csharp`.
5. No terminal do Orquestrador: `$env:ORQUESTRADOR_ROTAS_CSHARP = "1"` antes de rodar os scripts.
6. Conferir, comparando com a rota (mesma chamada com a variável em `0`):
   - leitura: `list_text_types`, `list_filled_region_types`, `room_areas`, `rooms_bbox` (nível do projeto),
     `viewport_boxes` (uma folha com vistas), `element_bbox_m` (uma parede e uma porta), `dims_list` (planta cotada);
   - escrita (em cópia/vista descartável): `create_drafting_view`, `set_view_scale`, `rename_views` (nome já usado
     → sufixo), `set_viewport_centers`, `place_schedule_on_sheet`, `set_annotation_crop`;
   - `tx_seguro`: `rota('Rotas','tx_seguro',['$doc',None,'rename_views'],{'kwargs':{'items':[...]}})` →
     `status: Committed`; e um erro proposital para ver `RolledBack` + `falhas` **sem diálogo**.
7. Voltar atrás: tirar a variável (`Remove-Item Env:ORQUESTRADOR_ROTAS_CSHARP`) — tudo volta para o pyRevit na
   hora. Para o add-in: fechar o Revit e recolocar as DLLs do backup (ou reinstalar a versão atual).
