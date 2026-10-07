"""Install the one-shot snapshot component in an already-running test player.

Uses Mono's exported embedding API on the game's main thread. Every expression
has a bounded timeout and the debugger always detaches after the request.
"""
import json
import shlex
import lldb


def capture(debugger, command, result, _):
    process = debugger.GetSelectedTarget().GetProcess()
    try:
        target = debugger.GetSelectedTarget()
        thread = process.GetThreadByIndexID(1)
        if not thread.IsValid():
            raise RuntimeError('No main thread in the target')
        process.SetSelectedThread(thread)
        frame = thread.GetFrameAtIndex(0)
        options = lldb.SBExpressionOptions()
        options.SetLanguage(lldb.eLanguageTypeC_plus_plus)
        options.SetStopOthers(False)
        options.SetTryAllThreads(True)
        options.SetTimeoutInMicroSeconds(5_000_000)
        options.SetUnwindOnError(True)
        options.SetIgnoreBreakpoints(True)

        def evaluate(code):
            value = frame.EvaluateExpression(code, options)
            if value.GetError().Fail():
                raise RuntimeError(value.GetError().GetCString())
            return value.GetValueAsUnsigned()

        def address(name):
            matches = target.FindSymbols(name)
            for i in range(matches.GetSize()):
                a = matches.GetContextAtIndex(i).GetSymbol().GetStartAddress().GetLoadAddress(target)
                if a != lldb.LLDB_INVALID_ADDRESS:
                    return a
            raise RuntimeError('Missing Mono export ' + name)

        path = shlex.split(command)[0]
        assembly = evaluate('((void*(*)(const char*,int*))%d)(%s,(int*)0)' % (address('mono_assembly_open'), json.dumps(path)))
        if not assembly:
            raise RuntimeError('The snapshot assembly did not load')
        image = evaluate('((void*(*)(void*))%d)((void*)%d)' % (address('mono_assembly_get_image'), assembly))
        cls = evaluate('((void*(*)(void*,const char*,const char*))%d)((void*)%d,"ULTRAKILL.MacPort.Fixtures","Capture")' % (address('mono_class_from_name'), image))
        if not cls:
            raise RuntimeError('The capture class was not found')
        method = evaluate('((void*(*)(void*,const char*,int))%d)((void*)%d,"Install",0)' % (address('mono_class_get_method_from_name'), cls))
        if not method:
            raise RuntimeError('The capture entry point was not found')
        exception = evaluate('({ void* ex=0; ((void*(*)(void*,void*,void**,void**))%d)((void*)%d,(void*)0,(void**)0,&ex); (unsigned long long)ex; })' % (address('mono_runtime_invoke'), method))
        if exception:
            raise RuntimeError('Capture installation raised a managed exception at ' + hex(exception))
        result.PutCString('One-shot snapshot installed. It will save on the next LateUpdate.')
    except Exception as error:
        result.SetError(str(error))
    finally:
        if process.IsValid() and process.GetState() == lldb.eStateStopped:
            error = process.Detach()
            if error.Fail():
                result.SetError('Could not detach: ' + error.GetCString())


def __lldb_init_module(debugger, _):
    debugger.HandleCommand('command script add -f live_fixture_lldb.capture fraud-capture')
