﻿using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;

namespace P.Runtime
{
    public enum PrtMachineStatus
    {
        Enabled,        // The state machine is enabled
        Blocked,        // The state machine is blocked on a dequeue or receive
        Halted,         // The state machine has halted
    };
    public enum PrtNextStatemachineOperation
    {
        EntryOperation,
        DequeueOperation,
        HandleEventOperation,
        ReceiveOperation
    };
    public enum PrtStateExitReason
    {
        NotExit,
        OnTransition,
        OnTransitionAfterExit,
        OnPopStatement,
        OnGotoStatement,
        OnUnhandledEvent
    };
    public enum PrtDequeueReturnStatus { SUCCESS, NULL, BLOCKED };

    public abstract class PrtImplMachine : PrtMachine
    {
        #region Fields
        public static int DefaultMaxBufferSize = int.MaxValue;
        public PrtEventBuffer eventQueue;
        public HashSet<PrtValue> receiveSet;
        public int maxBufferSize;
        public bool doAssume;
        public PrtInterfaceValue self;
        
        #endregion

        #region Clone and Undo
        public PrtImplMachine Clone()
        {
            var clonedMachine = MakeSkeleton();
            clonedMachine.instanceNumber = this.instanceNumber;
            foreach(var fd in fields)
            {
                clonedMachine.fields.Add(fd.Clone());
            }
            clonedMachine.eventValue = this.eventValue;
            clonedMachine.stateStack = this.stateStack.Clone();
            clonedMachine.invertedFunStack = this.invertedFunStack.Clone();
            clonedMachine.continuation = this.continuation.Clone();
            clonedMachine.currentTrigger = this.currentTrigger;
            clonedMachine.currentPayload = this.currentPayload.Clone();
            clonedMachine.eventQueue = this.eventQueue.Clone();
            foreach(var ev in this.receiveSet)
            {
                clonedMachine.receiveSet.Add(ev);
            }
            clonedMachine.currentStatus = this.currentStatus;
            clonedMachine.nextSMOperation = this.nextSMOperation;
            clonedMachine.stateExitReason = this.stateExitReason;
            clonedMachine.maxBufferSize = this.maxBufferSize;
            clonedMachine.doAssume = this.doAssume;
            clonedMachine.stateImpl = this.stateImpl;
            return clonedMachine;
        }
        #endregion

        #region Constructor
        public abstract PrtImplMachine MakeSkeleton();

        public PrtImplMachine() : base()
        {
            this.maxBufferSize = 0;
            this.doAssume = false;
            this.eventQueue = new PrtEventBuffer();
            this.receiveSet = new HashSet<PrtValue>();
        }

        public PrtImplMachine(StateImpl app, int maxBuff, bool assume) : base()
        {
            this.instanceNumber = this.NextInstanceNumber(app);
            this.eventQueue = new PrtEventBuffer();
            this.receiveSet = new HashSet<PrtValue>();
            this.maxBufferSize = maxBuff;
            this.doAssume = assume;
            this.stateImpl = app;
            this.self = new PrtInterfaceValue(this, new List<PrtEventValue>());
            //Push the start state function on the funStack.
            PrtPushState(StartState);
        }
        #endregion

        #region getters and setters
        public abstract int NextInstanceNumber(StateImpl app);
        #endregion

        #region State machine helper functions
        public void PrtResetTriggerAndPayload()
        {
            currentTrigger = PrtValue.@null;
            currentPayload = PrtValue.@null;
        }

        public override void PrtEnqueueEvent(PrtValue e, PrtValue arg, PrtMachine source, PrtMachineValue target = null)
        {
            
            PrtEventValue ev = e as PrtEventValue;
            if (ev.evt.name == "null")
            {
                throw new PrtIllegalEnqueueException("Enqueued event must not be null");
            }

            //check if the sent event is in source send set
            if(!source.sends.Contains(e as PrtEventValue))
            {
                throw new PrtIllegalEnqueueException(String.Format("Machine <0> cannot send event {1}", source.Name, e.ToString()));
            }

            //check if the sent event is in target permissions
            if(target is PrtInterfaceValue)
            {
                if(!(target as PrtInterfaceValue).permissions.Contains(e as PrtEventValue))
                {
                    throw new PrtIllegalEnqueueException(String.Format("Event {1} is not in the permission set of the target", e.ToString()));
                }
            }

            PrtType prtType = ev.evt.payloadType;

            //assertion to check if argument passed inhabits the payload type.
            if (prtType is PrtNullType)
            {
                if (!arg.Equals(PrtValue.@null))
                {
                    throw new PrtIllegalEnqueueException("Did not expect a payload value");
                }
            }
            else if (!PrtValue.PrtInhabitsType(arg, prtType))
            {
                throw new PrtInhabitsTypeException(String.Format("Payload <{0}> does not match the expected type <{1}> with event <{2}>", arg.ToString(), prtType.ToString(), ev.evt.name));
            }

            //check if the event sent is in the permissions

            if (currentStatus == PrtMachineStatus.Halted)
            {
                stateImpl.Trace(
                    @"<EnqueueLog> {0}-{1} Machine has been halted and Event {2} is dropped",
                    this.Name, this.instanceNumber, ev.evt.name);
            }
            else
            {
                stateImpl.Trace(
                    @"<EnqueueLog> Enqueued Event <{0}, {1}> in {2}-{3} by {4}-{5}",
                    ev.evt.name, arg.ToString(), this.Name, this.instanceNumber, source.Name, source.instanceNumber);
                this.eventQueue.EnqueueEvent(e, arg);
                if (this.maxBufferSize != DefaultMaxBufferSize && this.eventQueue.Size() > this.maxBufferSize)
                {
                    if (this.doAssume)
                    {
                        throw new PrtAssumeFailureException();
                    }
                    else
                    {
                        throw new PrtMaxBufferSizeExceededException(
                            String.Format(@"<EXCEPTION> Event Buffer Size Exceeded {0} in Machine {1}-{2}",
                            this.maxBufferSize, this.Name, this.instanceNumber));
                    }
                }
                if (currentStatus == PrtMachineStatus.Blocked && this.eventQueue.IsEnabled(this))
                {
                    currentStatus = PrtMachineStatus.Enabled;
                }
            }

            //Announce it to all the spec machines
            stateImpl.Announce(e as PrtEventValue, arg, source);
        }

        public PrtDequeueReturnStatus PrtDequeueEvent(bool hasNullTransition)
        {
            if (eventQueue.DequeueEvent(this))
            {
                if (currentStatus == PrtMachineStatus.Blocked)
                {
                    throw new PrtInternalException("Internal error: Tyring to execute blocked machine");
                }

                stateImpl.Trace(
                    "<DequeueLog> Dequeued Event <{0}, {1}> at Machine {2}-{3}",
                    (currentTrigger as PrtEventValue).evt.name, currentPayload.ToString(), Name, instanceNumber);
                receiveSet = new HashSet<PrtValue>();
                return PrtDequeueReturnStatus.SUCCESS;
            }
            else if (hasNullTransition || receiveSet.Contains(currentTrigger))
            {
                if (currentStatus == PrtMachineStatus.Blocked)
                {
                    throw new PrtInternalException("Internal error: Tyring to execute blocked machine");
                }
                stateImpl.Trace(
                    "<NullTransLog> Null transition taken by Machine {0}-{1}\n",
                    Name, instanceNumber);
                currentPayload = PrtValue.@null;
                currentTrigger = PrtValue.@null;
                receiveSet = new HashSet<PrtValue>();
                return PrtDequeueReturnStatus.NULL;
            }
            else
            {
                if (currentStatus == PrtMachineStatus.Blocked)
                {
                    throw new PrtAssumeFailureException();
                }
                currentStatus = PrtMachineStatus.Blocked;
                if (stateImpl.Deadlock)
                {
                    throw new PrtDeadlockException("Deadlock detected");
                }
                return PrtDequeueReturnStatus.BLOCKED;
            }
        }

        public void PrtExecuteReceiveCase(PrtValue ev)
        {
            var currRecIndex = continuation.receiveIndex;
            var currFun = invertedFunStack.TopOfStack.fun.receiveCases[currRecIndex][ev];
            if(currFun.IsAnonFun)
                PrtPushFunStackFrame(currFun, currFun.CreateLocals(currentPayload));
            else
                PrtPushFunStackFrame(currFun, currFun.CreateLocals());

            currFun.Execute(stateImpl, this);
        }

        public bool PrtIsPushTransitionPresent(PrtValue ev)
        {
            if (CurrentState.transitions.ContainsKey(ev))
                return CurrentState.transitions[ev].isPushTran;
            else
                return false;
        }

        public bool PrtHasNullReceiveCase()
        {
            return receiveSet.Contains(PrtValue.halt);
        }
        #endregion


        public void PrtRunStateMachine()
        {
            int numOfStepsTaken = 0;
            Debug.Assert(currentStatus == PrtMachineStatus.Enabled, "Invoked PrtRunStateMachine on a blocked or a halted machine");

            try
            {
                while (PrtStepStateMachine())
                {
                    if (numOfStepsTaken > 100000)
                    {
                        throw new PrtInfiniteRaiseLoop();
                    }                   
                    numOfStepsTaken++;
                }
            }
            catch(PrtException ex)
            {
                stateImpl.Exception = ex;
            }
            
        }
        
        public bool PrtStepStateMachine()
        {
            PrtValue currEventValue;
            PrtFun currAction;
            bool hasMoreWork = false;

            switch(nextSMOperation)
            {
                case PrtNextStatemachineOperation.EntryOperation:
                    goto DoEntry;
                case PrtNextStatemachineOperation.DequeueOperation:
                    goto DoDequeue;
                case PrtNextStatemachineOperation.HandleEventOperation:
                    goto DoHandleEvent;
                case PrtNextStatemachineOperation.ReceiveOperation:
                    goto DoReceive;
            }

            DoEntry:
            /*
             * Note that we have made an assumption that when a state is pushed on state stack or a transition is taken (update to a state)
             * the action set and deferred set is updated appropriately
            */
            if(invertedFunStack.TopOfStack == null)
            {
                stateImpl.Trace("<StateLog> Machine {0}-{1} entering State {2}", this.Name, this.instanceNumber, CurrentState.name);
                if(CurrentState.entryFun.IsAnonFun)
                    PrtPushFunStackFrame(CurrentState.entryFun, CurrentState.entryFun.CreateLocals(currentPayload));
                else
                    PrtPushFunStackFrame(CurrentState.entryFun, CurrentState.entryFun.CreateLocals());
            }
            //invoke the function
            invertedFunStack.TopOfStack.fun.Execute(stateImpl, this);
            goto CheckFunLastOperation;

            DoAction:
            currAction = PrtFindActionHandler(eventValue);
            if(currAction == PrtFun.IgnoreFun)
            {
                stateImpl.Trace("<ActionLog> Machine {0}-{1} ignoring Event {2} in State {3}", this.Name, this.instanceNumber, eventValue, CurrentState.name);
                PrtResetTriggerAndPayload();
            }
            else
            {
                if(invertedFunStack.TopOfStack == null)
                {
                    stateImpl.Trace("<ActionLog> Machine {0}-{1} executing action for Event {2} in State {3}", this.Name, this.instanceNumber, eventValue, CurrentState.name);
                    PrtPushFunStackFrame(currAction, currAction.CreateLocals(currentPayload));
                }
                //invoke the action handler
                invertedFunStack.TopOfStack.fun.Execute(stateImpl, this);
            }
            goto CheckFunLastOperation;

            CheckFunLastOperation:
            if(receiveSet.Count != 0)
            {
                // We are at a blocking "receive"; so do receive operation
                nextSMOperation = PrtNextStatemachineOperation.ReceiveOperation;
                goto Finish;

            }

            switch(continuation.reason)
            {
                case PrtContinuationReason.Pop:
                    {
                        stateExitReason = PrtStateExitReason.OnPopStatement;
                        PrtExecuteExitFunction();
                        goto CheckFunLastOperation;
                    }
                case PrtContinuationReason.Goto:
                    {
                        stateExitReason = PrtStateExitReason.OnGotoStatement;
                        PrtExecuteExitFunction();
                        goto CheckFunLastOperation;
                    }
                case PrtContinuationReason.Raise:
                    {
                        nextSMOperation = PrtNextStatemachineOperation.HandleEventOperation;
                        hasMoreWork = true;
                        goto Finish;
                    }
                case PrtContinuationReason.NewMachine:
                    {
                        stateExitReason = PrtStateExitReason.NotExit;
                        hasMoreWork = false;
                        goto Finish;
                    }
                case PrtContinuationReason.Nondet:
                    {
                        stateExitReason = PrtStateExitReason.NotExit;
                        stateImpl.SetPendingChoicesAsBoolean(this);
                        continuation.nondet = ((Boolean)stateImpl.GetSelectedChoiceValue(this));
                        hasMoreWork = false;
                        goto Finish;
                    }
                case PrtContinuationReason.Receive:
                    { 
                        stateExitReason = PrtStateExitReason.NotExit;
                        nextSMOperation = PrtNextStatemachineOperation.ReceiveOperation;
                        hasMoreWork = true;
                        goto Finish;
                    }
                case PrtContinuationReason.Send:
                    {
                        stateExitReason = PrtStateExitReason.NotExit;
                        hasMoreWork = false;
                        goto Finish;
                    }
                case PrtContinuationReason.Return:
                    {
                        switch (stateExitReason)
                        {
                            case PrtStateExitReason.NotExit:
                                {
                                    nextSMOperation = PrtNextStatemachineOperation.DequeueOperation;
                                    hasMoreWork = true;
                                    goto Finish;
                                }
                            case PrtStateExitReason.OnPopStatement:
                                {
                                    hasMoreWork = !PrtPopState(true);
                                    nextSMOperation = PrtNextStatemachineOperation.DequeueOperation;
                                    stateExitReason = PrtStateExitReason.NotExit;
                                    goto Finish;
                                }
                            case PrtStateExitReason.OnGotoStatement:
                                {
                                    PrtChangeState(destOfGoto);
                                    nextSMOperation = PrtNextStatemachineOperation.EntryOperation;
                                    stateExitReason = PrtStateExitReason.NotExit;
                                    hasMoreWork = true;
                                    goto Finish;
                                }
                            case PrtStateExitReason.OnUnhandledEvent:
                                {
                                    hasMoreWork = !PrtPopState(false);
                                    nextSMOperation = PrtNextStatemachineOperation.HandleEventOperation;
                                    stateExitReason = PrtStateExitReason.NotExit;
                                    goto Finish;
                                }
                            case PrtStateExitReason.OnTransition:
                                {
                                    stateExitReason = PrtStateExitReason.OnTransitionAfterExit;
                                    PrtExecuteTransitionFun(eventValue);
                                    goto CheckFunLastOperation;
                                }
                            case PrtStateExitReason.OnTransitionAfterExit:
                                {
                                    PrtChangeState(CurrentState.transitions[eventValue].gotoState);
                                    stateExitReason = PrtStateExitReason.NotExit;
                                    nextSMOperation = PrtNextStatemachineOperation.EntryOperation;
                                    hasMoreWork = true;
                                    goto Finish;
                                }
                            default:
                                {
                                    Debug.Assert(false, "Unexpected value for exit reason");
                                    goto Finish;
                                }
                        }
                        
                    }
                default:
                    {
                        Debug.Assert(false, "Unexpected value for continuation reason");
                        goto Finish;
                    }
            }

            DoDequeue:
            Debug.Assert(receiveSet.Count == 0, "Machine cannot be blocked at receive when here");
            var dequeueStatus = PrtDequeueEvent(CurrentState.hasNullTransition);
            if(dequeueStatus == PrtDequeueReturnStatus.BLOCKED)
            {
                nextSMOperation = PrtNextStatemachineOperation.DequeueOperation;
                hasMoreWork = false;
                goto Finish;

            }
            else if (dequeueStatus == PrtDequeueReturnStatus.SUCCESS)
            {
                nextSMOperation = PrtNextStatemachineOperation.HandleEventOperation;
                hasMoreWork = true;
                goto Finish;
            }
            else // NULL transition
            {
                nextSMOperation = PrtNextStatemachineOperation.HandleEventOperation;
                hasMoreWork = false;
                goto Finish;
            }


            DoHandleEvent:
            Debug.Assert(receiveSet.Count == 0, "The machine must not be blocked on a receive");
            if(!currentTrigger.Equals(PrtValue.@null))
            {
                currEventValue = currentTrigger;
                currentTrigger = PrtValue.@null;
            }
            else
            {
                currEventValue = eventValue;
            }

            if(PrtIsPushTransitionPresent(currEventValue))
            {
                PrtPushState(CurrentState.transitions[currEventValue].gotoState);
                goto DoEntry;
            }
            else if(PrtIsTransitionPresent(currEventValue))
            {
                stateExitReason = PrtStateExitReason.OnTransition;
                eventValue = currEventValue;
                PrtExecuteExitFunction();
                goto CheckFunLastOperation;
            }
            else if(PrtIsActionInstalled(currEventValue))
            {
                eventValue = currEventValue;
                goto DoAction;
            }
            else
            {
                stateExitReason = PrtStateExitReason.OnUnhandledEvent;
                eventValue = currEventValue;
                PrtExecuteExitFunction();
                goto CheckFunLastOperation;
            }

            DoReceive:
            Debug.Assert(receiveSet.Count == 0 && PrtHasNullReceiveCase(), "Receive set empty and at receive !!");
            if(receiveSet.Count == 0)
            {
                stateExitReason = PrtStateExitReason.NotExit;
                PrtExecuteReceiveCase(PrtValue.@null);
                goto CheckFunLastOperation;
            }
            dequeueStatus = PrtDequeueEvent(false);
            if (dequeueStatus == PrtDequeueReturnStatus.BLOCKED)
            {
                nextSMOperation = PrtNextStatemachineOperation.ReceiveOperation;
                hasMoreWork = false;
                goto Finish;

            }
            else if (dequeueStatus == PrtDequeueReturnStatus.SUCCESS)
            {
                stateExitReason = PrtStateExitReason.NotExit;
                PrtExecuteReceiveCase(currentTrigger);
                goto CheckFunLastOperation;
            }
            else // NULL case
            {
                nextSMOperation = PrtNextStatemachineOperation.ReceiveOperation;
                hasMoreWork = false;
                goto Finish;
            }

            Finish:
            Debug.Assert(!hasMoreWork || currentStatus == PrtMachineStatus.Enabled, "hasMoreWork is true but the statemachine is blocked");
            return hasMoreWork;

            }

    }
}
