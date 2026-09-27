# Supervisor e Worker: avaliação com GPUs separadas

Data: 26/09/2026. Base: `53575dd`. **Este relatório substitui a primeira matriz, que colocou os dois modelos na RTX 4090 por erro de configuração do estudo.** Nenhum prompt de produção foi alterado.

## Resposta curta

A variante C produziu mais resultados corretos e concluídos: **7/10**, contra **5/10** da variante B e **4/10** do prompt atual A. Também gerou artefatos corretos em **9/10**, mas o Native não concluiu nenhum de seus dois casos e o OpenCode bloqueou um caso por JSON malformado do Supervisor. É uma candidata para novo ensaio controlado, ainda sem evidência suficiente para adoção global ou prompts permanentes por harness.

## Configuração validada

Foram 30 execuções reais, sequenciais: cinco harnesses × duas tarefas × três variantes. A API e o diretório de dados eram isolados por variante; cada execução recebeu um workspace novo. O Supervisor `gpt-oss:20b` usou **AMD Radeon RX 7900 XTX, ROCm `rocm:0`**, e o Worker `qwen3.8:27b-gpu0` usou **NVIDIA RTX 4090, CUDA `ollama:0`**. O esforço ficou `high` no plano, `medium` no trabalho e verificação e `low` na conclusão. Não houve provedor de nuvem.

Antes de cada `start`, o executor conferiu nomes das duas GPUs, backends e endpoints distintos; qualquer divergência abortaria o run. Na primeira execução, os dois Ollama gerenciados coexistiram em `127.0.0.1:12434` (Worker) e `127.0.0.1:12435` (Supervisor). A memória dedicada por processo no Windows atribuiu aproximadamente 22,9 GB do runner do Worker à NVIDIA e 15,5 GB do runner do Supervisor ao LUID da AMD. Portanto, a nova matriz comprova também colocação física, não apenas os rótulos da rota.

As tarefas eram `ledger` (linhas `categoria,valor`, erros com número da linha, retorno `{total, categories}` com objeto ordenado, teste executado) e `numbers` (inteiros separados por vírgula, erro com posição, `sum`, `average([]) === null`, teste executado). Um verificador Node independente examinou os arquivos após cada run. **Sucesso estrito** exige estado Host `completed`, teste do Worker passando e teste independente passando. Os tetos fixos foram 600 s para `ledger` e 480 s para `numbers`; após o teto, houve cancelamento e espera pelo estado terminal.

- **A:** prompt atual.
- **B:** instrução de primeiro despacho concreto, critérios `MUST` observáveis e verificação dos arquivos em vez de confiar apenas no teste do Worker.
- **C:** B mais o objetivo original integral nos prompts do Worker e do verificador. A decomposição já recebia o objetivo nas três variantes.

## Resultados por harness

Legenda: **OK** = conclusão e ambos os testes corretos; **AC** = artefato correto, mas Host cancelado/bloqueado; **FC** = Host concluiu com artefato incorreto; **FI** = artefato incorreto ou incompleto e Host não concluiu. Tempo em segundos até terminal ou teto. Cada célula contém **uma execução**, não uma média.

| Harness | Tarefa | A | B | C |
|---|---|---:|---:|---:|
| Claude Code | ledger | OK 65 | OK 220 | OK 258 |
| Claude Code | numbers | FC 117 | FI 270 | OK 139 |
| Codex | ledger | FI 104 | FI 601 | OK 156 |
| Codex | numbers | FC 437 | OK 147 | OK 73 |
| Native | ledger | OK 161 | OK 199 | AC 602 |
| Native | numbers | FI 481 | AC 481 | AC 481 |
| OpenCode | ledger | FC 152 | FC 389 | FI 189 |
| OpenCode | numbers | OK 101 | OK 118 | OK 73 |
| Qwen Code | ledger | FI 203 | OK 185 | OK 189 |
| Qwen Code | numbers | OK 199 | AC 481 | OK 131 |

| Medida nas dez execuções | A | B | C |
|---|---:|---:|---:|
| Sucesso estrito | 4 | 5 | 7 |
| Artefato correto no teste independente | 4 | 7 | 9 |
| Conclusões do Host, inclusive falsos positivos | 7 | 6 | 7 |
| Mediana do tempo total, incluindo cancelamentos | 156 s | 245 s | 172 s |
| Mediana da decomposição | 40 s | 47 s | 37 s |
| Mediana do Worker registrada pelo Host | 89 s | 88 s | 102 s |

As medianas incluem observações censuradas pelos tetos e não estimam a duração de uma execução bem-sucedida. Houve uma execução por célula, sem ordem totalmente aleatória; temperaturas, residência dos modelos e variação de geração podem influir. A primeira matriz, com ambos os modelos na 4090, teve saídas diferentes em várias células e não deve ser combinada com esta para inferir velocidade ou taxa de sucesso.

## Achados que importam para o Supervisor

- **Plano pode consumir o trabalho inteiro.** Em B Claude Code `numbers`, o Supervisor gastou 266 s na decomposição e produziu um item inválido; o Worker teve zero tentativas. Em A Codex e Native `numbers`, as decomposições levaram aproximadamente 142 s e 141 s. Em C Codex `numbers`, caíram para 17 s, mas uma observação não prova causalidade do prompt.
- **Verificação pode inventar defeito.** Em B Qwen Code `numbers`, os arquivos passam nos dois testes, mas o Supervisor afirmou que `/^[+-]?\d+$/` não reconhecia inteiros e propôs substituí-la pela própria `/^[+-]?\d+$/`. A correção improdutiva consumiu o restante dos 480 s. O Supervisor deve relacionar rejeições a uma observação reproduzível do Host.
- **Teste do Worker não basta.** A produziu três falsos `completed`: Claude Code e Codex em `numbers`, OpenCode em `ledger`. B produziu um, OpenCode em `ledger`. C não produziu falso `completed` nesta amostra, mas bloqueou OpenCode `ledger` após o Supervisor retornar JSON canônico malformado.
- **Conclusão também é parte da performance.** Em C Native `ledger`, os dois itens foram aceitos e os arquivos passam nos testes, mas o run ficou em `completing` até 602 s; o gate registrou `blocked-validation-not-configured`. Em C Native `numbers`, os arquivos passam, porém o Worker repetiu uma resposta de planejamento inválida e o Host cancelou aos 481 s. Afinidade correta não resolve esses protocolos.
- **A fila ainda aceita item sem efeito.** Na primeira matriz, OpenCode C criou um item isolado de execução de teste com `evidencePaths: []`, apesar de o prompt proibir isso. `ValidateDecisionItem` aceita a lista vazia, mas a conclusão determinística exige caminho não vazio. Esse achado de código continua válido, embora não tenha reaparecido nesta matriz.

## Afinidade da RX 7900 XTX: diagnóstico separado

O checkout usa `AgenticRouter.Api/data/settings.json`. Antes da correção em 26/09, esse arquivo continha `gpt-oss:20b` e `qwen3.8:27b-gpu0` associados ao mesmo ID da RTX 4090; o último mtime observado era 24/09 às 22:56 UTC. O usuário informou que selecionou e salvou a 7900 XTX duas vezes em 24/09. **Não há evidência preservada de cada salvamento intermediário para explicar por que o valor retornou à 4090.** Não atribuo esse retorno apenas ao reinício nem descarto uma sobrescrita posterior.

Havia, porém, um defeito reproduzido na identidade AMD. Um checkpoint antigo do AR registrou a 7900 XTX como `dxgi-000000000001914b`; `/api/devices` antes da correção a registrou como `dxgi-0000000000018e8d`, enquanto o Windows mostrava a placa presente e saudável. O ID DXGI deriva de LUID transitório; uma afinidade antiga aparecia como “GPU indisponível” quando esse LUID mudava. Num diretório de dados descartável, o endpoint `/api/settings` salvou a afinidade e a recarregou corretamente após reiniciar somente a API **na mesma sessão do Windows**. Logo, o fluxo básico de gravação funciona, e o ID transitório explica pelo menos o aviso após mudança do LUID, mas não prova a causa da volta à 4090 no arquivo real.

Após aprovação do usuário, a correção foi aplicada ao checkout. Ela cruza a AMD DXGI com a única AMD PnP presente, de mesmo nome, e expõe o ID persistente `PCI\VEN_1002…` mantendo `rocm:0`. A afinidade real de `gpt-oss:20b` foi alterada atomicamente para esse ID; `qwen3.8:27b-gpu0` permaneceu na 4090. A API atualizada salvou e recarregou a configuração candidata após reinício, não gerou `model-gpu-unavailable` e preparou a rota Supervisor ROCm em `12435` e Worker CUDA em `12434`. Um smoke real adicional, separado da matriz, concluiu em 225 s com teste independente passando; os runners foram observados em GPUs físicas distintas. O build Release terminou com zero warnings, a formatação passou, os dois E2E focados de afinidade/persistência e GPU ausente passaram, e o diff foi inspecionado. Não houve reinício do Windows nem execução do AR com o diretório de dados real nesta validação.

## Próximos passos sugeridos

**Concluído nesta correção:** ID PnP da AMD aplicado ao checkout e afinidade de `gpt-oss:20b` salva na 7900 XTX; o valor em disco coincide com o dispositivo detectado e a rota foi confirmada com dados isolados. Se a afinidade voltar à 4090, registrar numa reprodução o valor antes/depois de cada `PUT /api/settings` e a operação posterior que regravar o arquivo, sem copiar o documento inteiro nem expor credenciais. A causa dessa possível sobrescrita continua sem prova direta.

**Útil no próximo experimento:** separar C em objetivo original só para o Worker e só para o verificador, com repetições intercaladas e tarefas reais. Isso isola se o ganho vem de implementação, verificação ou ambos. Corrigir a validação de `evidencePaths: []` antes de escolher prompt global. Investigar o gate de conclusão Native e a rejeição contraditória por regex com evidência de arquivo/teste, preservando a autoridade do Host.

**Provavelmente desnecessário agora:** reduzir globalmente `high`, criar uma política permanente de prompt por harness ou aumentar cegamente os limites de tempo. A mediana de decomposição de C foi 37 s, mas existiram planos de 100+ s e bloqueios em outras fases. Um limite maior esconderia loops; prompts específicos com apenas dois objetivos por harness seriam sobreajuste.

## Rastreabilidade

`.codex-build/supervisor-prompt-study-dual-gpu/` contém `study.py`, `seed-settings.json`, `aggregate.py`, `summary.json`, `summary.csv`, os 30 resultados individuais e os workspaces. O diretório é ignorado pelo Git. `aggregate.py` exige exatamente uma combinação de cada variante, tarefa e harness e rejeita qualquer rota fora das duas GPUs/endpoints definidos. A validação foi pela API, Host, modelos/harnesses reais e arquivos; a matriz não executou o navegador nem a suíte E2E completa. Após os runs, as APIs e os Ollama gerenciados do estudo foram encerrados; o Ollama do usuário, PID 14864, permaneceu em execução.
